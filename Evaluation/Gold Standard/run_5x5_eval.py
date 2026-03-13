"""
ARIS 5×5 Gold Standard Evaluation — uploads 5 resumes × 5 job postings,
runs all 25 match analyses, and produces hypothesis-supporting figures.

Usage:
  python run_5x5_eval.py [--api apiurl]
"""

import os, sys, json, time, argparse, textwrap
import requests
import pypdf
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
import matplotlib.ticker as mticker
import numpy as np

SCRIPT_DIR   = os.path.dirname(os.path.abspath(__file__))
RESUME_DIR   = os.path.join(SCRIPT_DIR, "New Documents", "PDFS")
JOB_DIR      = os.path.join(SCRIPT_DIR, "New Documents", "Job Postings")
RESULTS_DIR  = os.path.join(SCRIPT_DIR, "Results")
os.makedirs(RESULTS_DIR, exist_ok=True)

DEFAULT_API = "http://localhost:5000"

DOMAINS = ["BackendPython", "DataEngineer", "DotNET", "DevOps", "Frontend"]
DOMAIN_LABELS = {
    "BackendPython":  "Backend\nPython",
    "DataEngineer":   "Data\nEngineer",
    "DotNET":         "DotNET",
    "DevOps":         "DevOps",
    "Frontend":       "Frontend",
}

HYPOTHESIS_THRESHOLD = 0.15   # ≥15% graph-only structural discovery required


def pdf_to_text(path: str) -> str:
    reader = pypdf.PdfReader(path)
    return "\n".join(page.extract_text() or "" for page in reader.pages).strip()


def post(api_base: str, path: str, payload: dict, retries: int = 3) -> dict:
    url = f"{api_base}{path}"
    headers = {
        "Content-Type": "application/json",
        "ngrok-skip-browser-warning": "true",
    }
    for attempt in range(1, retries + 1):
        try:
            r = requests.post(url, json=payload, headers=headers, timeout=600)
            r.raise_for_status()
            return r.json()
        except Exception as e:
            if attempt == retries:
                raise RuntimeError(f"POST {url} failed after {retries} attempts: {e}") from e
            wait = 5 * attempt
            print(f"  [retry {attempt}/{retries}] error: {e}  — waiting {wait}s")
            time.sleep(wait)


def upload_resumes(api: str) -> dict[str, str]:
    print("\n=== Uploading Resumes (PDF) ===")
    ids = {}
    for domain in DOMAINS:
        path = os.path.join(RESUME_DIR, f"Resume_{domain}.pdf")
        text = pdf_to_text(path)
        user_id = f"gs-eval-{domain.lower()}"
        print(f"  [{domain}] uploading ({len(text)} chars) as userId={user_id} ...")
        result = post(api, "/api/resume/upload-text", {"content": text, "userId": user_id})
        profile_id = str(result["id"])
        ids[domain] = profile_id
        print(f"  [{domain}] profile_id = {profile_id}")
    return ids


def upload_jobs(api: str) -> dict[str, str]:
    print("\n=== Uploading Job Postings ===")
    ids = {}
    for domain in DOMAINS:
        path = os.path.join(JOB_DIR, f"JobPosting_{domain}.txt")
        with open(path, encoding="utf-8") as f:
            text = f.read()
        recruiter_id = f"gs-eval-recruiter-{domain.lower()}"
        print(f"  [{domain}] uploading ({len(text)} chars) ...")
        result = post(api, "/api/job", {"description": text, "recruiterId": recruiter_id})
        job_id = str(result["jobId"])
        ids[domain] = job_id
        print(f"  [{domain}] job_id = {job_id}")
    return ids


def run_matrix(api: str, resume_ids: dict, job_ids: dict) -> dict:
    """Returns nested dict: results[resume_domain][job_domain] = MatchAnalysisResult"""
    print("\n=== Running 25 Match Analyses ===")
    results = {}
    total = len(DOMAINS) * len(DOMAINS)
    done = 0
    for r_domain in DOMAINS:
        results[r_domain] = {}
        for j_domain in DOMAINS:
            done += 1
            label = f"{r_domain} → {j_domain}"
            print(f"  [{done:02d}/{total}] {label} ...", end=" ", flush=True)
            t0 = time.time()
            data = post(api, "/api/match/analyze", {
                "userProfileId": resume_ids[r_domain],
                "jobId": job_ids[j_domain],
            })
            elapsed = time.time() - t0
            results[r_domain][j_domain] = data
            aris = data.get("arisScore", 0)
            t1 = len(data.get("matchingSkills", []))
            t2 = len(data.get("implicitlyDiscoveredSkills", []))
            t3 = len(data.get("prerequisiteMetSkills", []))
            t4 = len(data.get("bridgeableSkills", []))
            t5 = len(data.get("hardGaps", []))
            print(f"ArisScore={aris:.3f}  T1={t1} T2={t2} T3={t3} T4={t4} T5={t5}  ({elapsed:.1f}s)")
    return results


def compute_metrics(results: dict) -> dict:
    metrics = {}
    for r_dom in DOMAINS:
        metrics[r_dom] = {}
        for j_dom in DOMAINS:
            d = results[r_dom][j_dom]
            t1 = len(d.get("matchingSkills", []))
            t2 = len(d.get("implicitlyDiscoveredSkills", []))
            t3 = len(d.get("prerequisiteMetSkills", []))
            t4 = len(d.get("bridgeableSkills", []))
            t5 = len(d.get("hardGaps", []))
            total = t1 + t2 + t3 + t4 + t5
            graph_sdr = (t2 + t3 + t4) / total if total > 0 else 0.0
            full_cov  = (t1 + t2 + t3 + t4) / total if total > 0 else 0.0
            metrics[r_dom][j_dom] = {
                "aris_score":        d.get("arisScore", 0),
                "vector_similarity": d.get("vectorSimilarity", 0),
                "t1": t1, "t2": t2, "t3": t3, "t4": t4, "t5": t5,
                "total_job_skills": total,
                "graph_sdr":   graph_sdr,
                "full_coverage": full_cov,
            }
    return metrics


TIER_COLORS = {
    "T1": "#2ecc71",
    "T2": "#1abc9c",
    "T3": "#f1c40f",
    "T4": "#e67e22",
    "T5": "#e74c3c",
}

SHORT = [DOMAIN_LABELS[d] for d in DOMAINS]


def fig_aris_heatmap(metrics: dict, out_dir: str):
    mat = np.array([
        [metrics[r][j]["aris_score"] for j in DOMAINS]
        for r in DOMAINS
    ])

    fig, ax = plt.subplots(figsize=(7, 5.5))
    im = ax.imshow(mat, cmap="YlGn", vmin=0, vmax=1, aspect="auto")
    plt.colorbar(im, ax=ax, label="ArisScore")

    ax.set_xticks(range(5))
    ax.set_yticks(range(5))
    ax.set_xticklabels([DOMAIN_LABELS[d] for d in DOMAINS], fontsize=9)
    ax.set_yticklabels([DOMAIN_LABELS[d] for d in DOMAINS], fontsize=9)
    ax.set_xlabel("Job Posting Domain", fontsize=10, labelpad=8)
    ax.set_ylabel("Resume Domain", fontsize=10, labelpad=8)
    ax.set_title("ArisScore — 5×5 Match Matrix\n(Higher = Better Fit)", fontsize=11, pad=12)

    for i in range(5):
        for j in range(5):
            val = mat[i, j]
            color = "white" if val < 0.45 else "black"
            weight = "bold" if i == j else "normal"
            ax.text(j, i, f"{val:.3f}", ha="center", va="center",
                    fontsize=8.5, color=color, fontweight=weight)

    for k in range(5):
        ax.add_patch(plt.Rectangle((k - 0.5, k - 0.5), 1, 1,
                                   fill=False, edgecolor="#2c3e50", linewidth=2.5))

    plt.tight_layout()
    path = os.path.join(out_dir, "fig1_aris_heatmap.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def fig_graph_sdr_diagonal(metrics: dict, out_dir: str):
    sdrs   = [metrics[d][d]["graph_sdr"] for d in DOMAINS]
    colors = ["#27ae60" if s >= HYPOTHESIS_THRESHOLD else "#e74c3c" for s in sdrs]
    labels = [DOMAIN_LABELS[d] for d in DOMAINS]

    fig, ax = plt.subplots(figsize=(7, 4.5))
    bars = ax.bar(labels, sdrs, color=colors, edgecolor="white", linewidth=0.8, width=0.55)

    ax.axhline(HYPOTHESIS_THRESHOLD, color="#c0392b", linewidth=1.5,
               linestyle="--", label=f"Hypothesis threshold ({int(HYPOTHESIS_THRESHOLD*100)}%)")

    for bar, val in zip(bars, sdrs):
        ax.text(bar.get_x() + bar.get_width() / 2, val + 0.008,
                f"{val:.0%}", ha="center", va="bottom", fontsize=9.5, fontweight="bold")

    ax.set_ylim(0, max(max(sdrs) + 0.12, HYPOTHESIS_THRESHOLD + 0.12))
    ax.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))
    ax.set_ylabel("Graph-Only Structural Discovery Rate\n(Tiers 2–4 / Total Required Skills)", fontsize=9.5)
    ax.set_title("Graph Traversal Discovery — Domain-Correct Pairs\n"
                 "(Skills identified via graph that vector similarity alone would miss)", fontsize=10, pad=10)
    ax.legend(fontsize=9)
    ax.spines[["top", "right"]].set_visible(False)

    pass_count = sum(1 for s in sdrs if s >= HYPOTHESIS_THRESHOLD)
    verdict = "SUPPORTED" if pass_count == 5 else f"PARTIAL ({pass_count}/5 pass)"
    color = "#27ae60" if pass_count == 5 else "#e67e22"
    ax.text(0.98, 0.97, f"Hypothesis: {verdict}", transform=ax.transAxes,
            ha="right", va="top", fontsize=9, color=color, fontweight="bold",
            bbox=dict(boxstyle="round,pad=0.3", facecolor="white", edgecolor=color, alpha=0.9))

    plt.tight_layout()
    path = os.path.join(out_dir, "fig2_graph_sdr_diagonal.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def fig_tier_composition_diagonal(metrics: dict, out_dir: str):
    labels  = [DOMAIN_LABELS[d] for d in DOMAINS]
    t1_vals = [metrics[d][d]["t1"] for d in DOMAINS]
    t2_vals = [metrics[d][d]["t2"] for d in DOMAINS]
    t3_vals = [metrics[d][d]["t3"] for d in DOMAINS]
    t4_vals = [metrics[d][d]["t4"] for d in DOMAINS]
    t5_vals = [metrics[d][d]["t5"] for d in DOMAINS]

    x     = np.arange(5)
    width = 0.55

    fig, ax = plt.subplots(figsize=(8, 5))
    b1 = ax.bar(x, t1_vals, width, label="Tier 1 — Direct Match",       color=TIER_COLORS["T1"])
    b2 = ax.bar(x, t2_vals, width, bottom=t1_vals, label="Tier 2 — Implicit",  color=TIER_COLORS["T2"])
    b3 = ax.bar(x, t3_vals, width,
                bottom=[a+b for a,b in zip(t1_vals, t2_vals)],
                label="Tier 3 — Prerequisite Met", color=TIER_COLORS["T3"])
    b4 = ax.bar(x, t4_vals, width,
                bottom=[a+b+c for a,b,c in zip(t1_vals, t2_vals, t3_vals)],
                label="Tier 4 — Bridgeable", color=TIER_COLORS["T4"])
    b5 = ax.bar(x, t5_vals, width,
                bottom=[a+b+c+d for a,b,c,d in zip(t1_vals, t2_vals, t3_vals, t4_vals)],
                label="Tier 5 — Hard Gap", color=TIER_COLORS["T5"])

    ax.set_xticks(x)
    ax.set_xticklabels(labels, fontsize=10)
    ax.set_ylabel("Number of Required Skills", fontsize=10)
    ax.set_title("Skill Coverage Tier Breakdown — Domain-Correct Pairs", fontsize=11, pad=10)
    ax.legend(loc="upper right", fontsize=8.5, framealpha=0.9)
    ax.spines[["top", "right"]].set_visible(False)

    totals = [a+b+c+d+e for a,b,c,d,e in zip(t1_vals,t2_vals,t3_vals,t4_vals,t5_vals)]
    for xi, tot in zip(x, totals):
        ax.text(xi, tot + 0.3, str(tot), ha="center", va="bottom", fontsize=9, fontweight="bold")

    plt.tight_layout()
    path = os.path.join(out_dir, "fig3_tier_composition_diagonal.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def fig_diagonal_vs_offdiagonal(metrics: dict, out_dir: str):
    diag_aris = [metrics[d][d]["aris_score"] for d in DOMAINS]
    off_aris  = [metrics[r][j]["aris_score"]
                 for r in DOMAINS for j in DOMAINS if r != j]

    diag_sdr  = [metrics[d][d]["graph_sdr"] for d in DOMAINS]
    off_sdr   = [metrics[r][j]["graph_sdr"]
                 for r in DOMAINS for j in DOMAINS if r != j]

    fig, axes = plt.subplots(1, 2, figsize=(10, 5))

    def box(ax, diag, off, title, ylabel, threshold=None):
        bp = ax.boxplot(
            [diag, off],
            labels=["Correct-Domain\nPairs (n=5)", "Cross-Domain\nPairs (n=20)"],
            patch_artist=True,
            medianprops=dict(color="black", linewidth=2),
        )
        colors = ["#27ae60", "#aab7b8"]
        for patch, color in zip(bp["boxes"], colors):
            patch.set_facecolor(color)
            patch.set_alpha(0.8)

        for i, vals in enumerate([diag, off], start=1):
            jitter = np.random.default_rng(42).uniform(-0.07, 0.07, len(vals))
            ax.scatter([i + j for j in jitter], vals,
                       color="white", edgecolor="#2c3e50", s=40, zorder=5, linewidth=0.8)

        if threshold is not None:
            ax.axhline(threshold, color="#c0392b", linewidth=1.4, linestyle="--",
                       label=f"Threshold ({threshold:.0%})")
            ax.legend(fontsize=8.5)

        ax.set_title(title, fontsize=10, pad=8)
        ax.set_ylabel(ylabel, fontsize=9.5)
        ax.spines[["top", "right"]].set_visible(False)

    box(axes[0], diag_aris, off_aris,
        "ArisScore Distribution\nCorrect vs Cross-Domain",
        "ArisScore")

    box(axes[1], diag_sdr, off_sdr,
        "Graph Structural Discovery Rate\nCorrect vs Cross-Domain",
        "Graph SDR (Tiers 2–4 / Total)",
        threshold=HYPOTHESIS_THRESHOLD)

    axes[1].yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))

    fig.suptitle("Domain-Correct vs Cross-Domain Performance", fontsize=12, y=1.01)
    plt.tight_layout()
    path = os.path.join(out_dir, "fig4_diagonal_vs_offdiagonal.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def fig_cumulative_coverage(metrics: dict, out_dir: str):
    fig, ax = plt.subplots(figsize=(8, 5))

    colors_by_domain = {
        "BackendPython":  "#3498db",
        "DataEngineer":   "#e67e22",
        "DotNET":         "#27ae60",
        "DevOps":         "#9b59b6",
        "Frontend":       "#e74c3c",
    }

    x_labels = ["T1 only\n(vector)", "T1+T2\n(+implicit)", "T1+T2+T3\n(+prereq)", "T1+T2+T3+T4\n(+bridge)"]

    for d in DOMAINS:
        m = metrics[d][d]
        total = m["total_job_skills"]
        if total == 0:
            continue
        cum = [
            m["t1"] / total,
            (m["t1"] + m["t2"]) / total,
            (m["t1"] + m["t2"] + m["t3"]) / total,
            (m["t1"] + m["t2"] + m["t3"] + m["t4"]) / total,
        ]
        label = DOMAIN_LABELS[d].replace("\n", " ")
        ax.plot(range(4), cum, marker="o", linewidth=2,
                color=colors_by_domain[d], label=label, markersize=6)
        ax.annotate(f"{cum[-1]:.0%}", xy=(3, cum[-1]), xytext=(3.06, cum[-1]),
                    fontsize=8.5, color=colors_by_domain[d], va="center")

    ax.axhline(HYPOTHESIS_THRESHOLD, color="#c0392b", linewidth=1.4,
               linestyle="--", label=f"20% SDR threshold\n(graph-only contribution)", alpha=0.8)

    ax.set_xticks(range(4))
    ax.set_xticklabels(x_labels, fontsize=9)
    ax.set_xlim(-0.2, 3.6)
    ax.set_ylim(0, 1.05)
    ax.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))
    ax.set_ylabel("Cumulative % of Required Skills Covered", fontsize=10)
    ax.set_title("Cumulative Skill Coverage by Tier — Domain-Correct Pairs\n"
                 "(Each step shows the additive value of graph traversal)", fontsize=10, pad=10)
    ax.legend(fontsize=9, loc="upper left")
    ax.spines[["top", "right"]].set_visible(False)

    ax.axvspan(0.5, 3.5, alpha=0.05, color="#3498db", label="_nolegend_")
    ax.text(2.0, 0.03, "Graph traversal region", ha="center", fontsize=8.5,
            color="#3498db", style="italic")

    plt.tight_layout()
    path = os.path.join(out_dir, "fig5_cumulative_coverage.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def fig_sdr_heatmap(metrics: dict, out_dir: str):
    mat = np.array([
        [metrics[r][j]["graph_sdr"] for j in DOMAINS]
        for r in DOMAINS
    ])

    fig, ax = plt.subplots(figsize=(7, 5.5))
    im = ax.imshow(mat, cmap="Blues", vmin=0, vmax=max(mat.max(), HYPOTHESIS_THRESHOLD + 0.05), aspect="auto")
    cbar = plt.colorbar(im, ax=ax)
    cbar.set_label("Graph SDR (Tiers 2–4 / Total)", fontsize=9)
    cbar.ax.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))

    ax.set_xticks(range(5))
    ax.set_yticks(range(5))
    ax.set_xticklabels([DOMAIN_LABELS[d] for d in DOMAINS], fontsize=9)
    ax.set_yticklabels([DOMAIN_LABELS[d] for d in DOMAINS], fontsize=9)
    ax.set_xlabel("Job Posting Domain", fontsize=10, labelpad=8)
    ax.set_ylabel("Resume Domain", fontsize=10, labelpad=8)
    ax.set_title("Graph-Only Structural Discovery Rate — Full 5×5 Matrix\n"
                 "(Tiers 2+3+4 / Total Required Skills)", fontsize=10, pad=12)

    for i in range(5):
        for j in range(5):
            val = mat[i, j]
            text_color = "white" if val > 0.5 else "black"
            weight = "bold" if i == j else "normal"
            marker = " ✓" if (i == j and val >= HYPOTHESIS_THRESHOLD) else ""
            ax.text(j, i, f"{val:.0%}{marker}", ha="center", va="center",
                    fontsize=8.5, color=text_color, fontweight=weight)

    for k in range(5):
        ax.add_patch(plt.Rectangle((k - 0.5, k - 0.5), 1, 1,
                                   fill=False, edgecolor="#2c3e50", linewidth=2.5))

    plt.tight_layout()
    path = os.path.join(out_dir, "fig6_sdr_heatmap.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def print_summary(metrics: dict):
    print("\n" + "="*65)
    print("  HYPOTHESIS TEST SUMMARY")
    print("  'Graph traversal identifies ≥20% of domain-correct job skills'")
    print("="*65)

    print(f"\n{'Domain Pair':<22} {'ArisScore':>10} {'Graph SDR':>10} {'T1':>4} {'T2':>4} {'T3':>4} {'T4':>4} {'T5':>4} {'Pass':>5}")
    print("-"*65)

    pass_count = 0
    for d in DOMAINS:
        m = metrics[d][d]
        sdr   = m["graph_sdr"]
        ok    = sdr >= HYPOTHESIS_THRESHOLD
        if ok: pass_count += 1
        label = f"{d[:14]} → {d[:14]}"
        print(f"  {label:<20} {m['aris_score']:>10.3f} {sdr:>9.1%}"
              f" {m['t1']:>4} {m['t2']:>4} {m['t3']:>4} {m['t4']:>4} {m['t5']:>4}"
              f"  {'✓' if ok else '✗'}")

    print("-"*65)
    verdict = "SUPPORTED" if pass_count == 5 else f"PARTIAL ({pass_count}/5)"
    print(f"\n  Result: {pass_count}/5 domain-correct pairs meet the ≥20% threshold.")
    print(f"  Hypothesis: {verdict}\n")

    print("  Off-diagonal ArisScore (mean / max per resume row):")
    for r in DOMAINS:
        off_scores = [metrics[r][j]["aris_score"] for j in DOMAINS if j != r]
        diag_score =  metrics[r][r]["aris_score"]
        print(f"    {r:<22} diag={diag_score:.3f}  off-mean={np.mean(off_scores):.3f}  margin={diag_score - np.mean(off_scores):+.3f}")
    print()


def save_raw_results(results: dict, metrics: dict, out_dir: str):
    path = os.path.join(out_dir, "5x5_raw_results.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"results": results, "metrics": metrics}, f, indent=2)
    print(f"  Raw data saved: {path}")


# Maps fixture_uuids.json snake_case keys → DOMAINS PascalCase keys
_FIXTURE_KEY_MAP = {
    "backend_python": "BackendPython",
    "data_engineer":  "DataEngineer",
    "dotnet":         "DotNET",
    "devops":         "DevOps",
    "frontend":       "Frontend",
}

DEFAULT_FIXTURE_UUIDS = os.path.join(SCRIPT_DIR, "fixture_uuids.json")


def load_fixture_uuids(path: str) -> tuple[dict, dict]:
    """Load fixture_uuids.json and remap keys to PascalCase."""
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    resume_ids = {_FIXTURE_KEY_MAP[k]: v for k, v in data["resumes"].items() if k in _FIXTURE_KEY_MAP}
    job_ids    = {_FIXTURE_KEY_MAP[k]: v for k, v in data["jobs"].items()    if k in _FIXTURE_KEY_MAP}
    missing = [d for d in DOMAINS if d not in resume_ids or d not in job_ids]
    if missing:
        raise ValueError(f"fixture_uuids.json is missing entries for: {missing}")
    return resume_ids, job_ids


def main():
    parser = argparse.ArgumentParser(description="ARIS 5×5 Gold Standard Evaluation")
    parser.add_argument("--api", default=DEFAULT_API, help="API base URL")
    parser.add_argument("--skip-upload", action="store_true",
                        help="Skip upload phase and load IDs from previous run (Results/uploaded_ids.json)")
    parser.add_argument("--fixture-uuids", metavar="PATH", default=DEFAULT_FIXTURE_UUIDS,
                        help="Load fixture UUIDs from this file instead of uploading. "
                             f"Defaults to: {DEFAULT_FIXTURE_UUIDS}")
    args = parser.parse_args()

    api = args.api.rstrip("/")
    ids_path = os.path.join(RESULTS_DIR, "uploaded_ids.json")

    print(f"\nARIS 5×5 Gold Standard Evaluation")
    print(f"API: {api}")
    print(f"Resumes: {RESUME_DIR}")
    print(f"Results: {RESULTS_DIR}")

    fixture_path = os.path.normpath(os.path.join(SCRIPT_DIR, args.fixture_uuids)) \
        if not os.path.isabs(args.fixture_uuids) else args.fixture_uuids
    if os.path.exists(fixture_path):
        print(f"\n[fixture-uuids] Loading IDs from: {fixture_path}")
        resume_ids, job_ids = load_fixture_uuids(fixture_path)
        for d in DOMAINS:
            print(f"  {d}: resume={resume_ids[d]}  job={job_ids[d]}")
    elif args.skip_upload and os.path.exists(ids_path):
        print("\n[skip-upload] Loading IDs from previous run ...")
        with open(ids_path) as f:
            saved = json.load(f)
        resume_ids = saved["resume_ids"]
        job_ids    = saved["job_ids"]
    else:
        resume_ids = upload_resumes(api)
        job_ids    = upload_jobs(api)
        _reverse_map = {v: k for k, v in _FIXTURE_KEY_MAP.items()}
        fixture_out = os.path.normpath(DEFAULT_FIXTURE_UUIDS)
        os.makedirs(os.path.dirname(fixture_out), exist_ok=True)
        fixture_data = {
            "resumes": {_reverse_map[d]: resume_ids[d] for d in DOMAINS},
            "jobs":    {_reverse_map[d]: job_ids[d]    for d in DOMAINS},
        }
        with open(fixture_out, "w", encoding="utf-8") as f:
            json.dump(fixture_data, f, indent=2)
        print(f"\n  IDs saved to: {fixture_out}")

    results = run_matrix(api, resume_ids, job_ids)
    metrics = compute_metrics(results)
    print_summary(metrics)

    print("=== Generating Figures ===")
    fig_aris_heatmap(metrics, RESULTS_DIR)
    fig_graph_sdr_diagonal(metrics, RESULTS_DIR)
    fig_tier_composition_diagonal(metrics, RESULTS_DIR)
    fig_diagonal_vs_offdiagonal(metrics, RESULTS_DIR)
    fig_cumulative_coverage(metrics, RESULTS_DIR)
    fig_sdr_heatmap(metrics, RESULTS_DIR)

    save_raw_results(results, metrics, RESULTS_DIR)

    print("\nDone. All figures and data saved to:", RESULTS_DIR)


if __name__ == "__main__":
    main()
