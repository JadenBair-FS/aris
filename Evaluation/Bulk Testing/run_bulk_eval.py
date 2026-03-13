"""
ARIS Bulk Evaluation — uploads 50 resumes × 25 job postings across 5 domains,
runs all 1,250 match analyses, and produces thesis figures.

Usage:
  python run_bulk_eval.py [--api apiurl]
"""

import os, sys, json, time, argparse
import requests
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
import matplotlib.ticker as mticker
import numpy as np
from datetime import datetime

SCRIPT_DIR  = os.path.dirname(os.path.abspath(__file__))
RESUMES_DIR = os.path.join(SCRIPT_DIR, "Resumes")
JOBS_DIR    = os.path.join(SCRIPT_DIR, "Job Postings")
RESULTS_DIR = os.path.join(SCRIPT_DIR, "Results")
os.makedirs(RESULTS_DIR, exist_ok=True)

DEFAULT_API = "http://localhost:5000"

DOMAINS = ["BackendPython", "DataEngineer", "DotNET", "DevOps", "Frontend"]
DOMAIN_LABELS = {
    "BackendPython": "Backend\nPython",
    "DataEngineer":  "Data\nEngineer",
    "DotNET":        "DotNET",
    "DevOps":        "DevOps",
    "Frontend":      "Frontend",
}

HYPOTHESIS_THRESHOLD = 0.15   # ≥15% graph-only structural discovery required

PIPELINE_RESULTS_DIR = os.path.join(SCRIPT_DIR, "pipeline_results")


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


def upload_resumes(api: str) -> dict[str, list[str]]:
    print("\n=== Uploading Resumes (10 per domain) ===")
    ids = {}
    for domain in DOMAINS:
        ids[domain] = []
        domain_dir = os.path.join(RESUMES_DIR, domain)
        files = sorted([f for f in os.listdir(domain_dir) if f.endswith(".txt")])
        for f_name in files:
            path = os.path.join(domain_dir, f_name)
            with open(path, encoding="utf-8") as f:
                text = f.read()
            user_id = f"bulk-eval-resume-{domain.lower().replace(' ', '-')}-{f_name.split('_')[0]}"
            print(f"  [{domain}] uploading {f_name} as userId={user_id} ...", end=" ", flush=True)
            t0 = time.time()
            result = post(api, "/api/resume/upload-text", {"content": text, "userId": user_id})
            profile_id = str(result["id"])
            ids[domain].append(profile_id)
            print(f"profile_id={profile_id} ({time.time()-t0:.1f}s)")
    return ids


def upload_jobs(api: str) -> dict[str, list[str]]:
    print("\n=== Uploading Job Postings (5 per domain) ===")
    ids = {}
    for domain in DOMAINS:
        ids[domain] = []
        domain_dir = os.path.join(JOBS_DIR, domain)
        files = sorted([f for f in os.listdir(domain_dir) if f.endswith(".txt")])
        for f_name in files:
            path = os.path.join(domain_dir, f_name)
            with open(path, encoding="utf-8") as f:
                text = f.read()
            recruiter_id = f"bulk-eval-recruiter-{domain.lower().replace(' ', '-')}-{f_name.split('_')[0]}"
            print(f"  [{domain}] uploading {f_name} ...", end=" ", flush=True)
            t0 = time.time()
            result = post(api, "/api/job", {"description": text, "recruiterId": recruiter_id})
            job_id = str(result["jobId"])
            ids[domain].append(job_id)
            print(f"job_id={job_id} ({time.time()-t0:.1f}s)")
    return ids


def run_matrix(api: str, resume_ids: dict, job_ids: dict) -> dict:
    """Returns results[r_domain][j_domain] = [MatchAnalysisResult, ...]"""
    print("\n=== Running 1,250 Match Analyses ===")
    results = {}
    total_resumes = sum(len(v) for v in resume_ids.values())
    total_jobs = sum(len(v) for v in job_ids.values())
    total = total_resumes * total_jobs
    done = 0
    
    for r_domain in DOMAINS:
        results[r_domain] = {}
        for j_domain in DOMAINS:
            results[r_domain][j_domain] = []
            print(f"\n--- Domain Pair: {r_domain} -> {j_domain} ---")
            for r_id in resume_ids[r_domain]:
                for j_id in job_ids[j_domain]:
                    done += 1
                    label = f"[{done:04d}/{total}] {r_id} → {j_id}"
                    print(f"  {label} ...", end=" ", flush=True)
                    t0 = time.time()
                    data = post(api, "/api/match/analyze", {
                        "userProfileId": r_id,
                        "jobId": j_id,
                    })
                    elapsed = time.time() - t0
                    results[r_domain][j_domain].append(data)
                    aris = data.get("arisScore", 0)
                    t2 = len(data.get("implicitlyDiscoveredSkills", []))
                    t3 = len(data.get("prerequisiteMetSkills", []))
                    t4 = len(data.get("bridgeableSkills", []))
                    print(f"ArisScore={aris:.3f} SDR={t2+t3+t4} ({elapsed:.1f}s)")
    return results


def compute_metrics(results: dict) -> dict:
    metrics = {}
    for r_dom in DOMAINS:
        metrics[r_dom] = {}
        for j_dom in DOMAINS:
            metrics[r_dom][j_dom] = []
            for d in results[r_dom][j_dom]:
                t1 = len(d.get("matchingSkills", []))
                t2 = len(d.get("implicitlyDiscoveredSkills", []))
                t3 = len(d.get("prerequisiteMetSkills", []))
                t4 = len(d.get("bridgeableSkills", []))
                t5 = len(d.get("hardGaps", []))
                total = t1 + t2 + t3 + t4 + t5
                graph_sdr = (t2 + t3 + t4) / total if total > 0 else 0.0
                full_cov  = (t1 + t2 + t3 + t4) / total if total > 0 else 0.0
                
                metrics[r_dom][j_dom].append({
                    "aris_score":        d.get("arisScore", 0),
                    "vector_similarity": d.get("vectorSimilarity", 0),
                    "t1": t1, "t2": t2, "t3": t3, "t4": t4, "t5": t5,
                    "total_job_skills": total,
                    "graph_sdr":   graph_sdr,
                    "full_coverage": full_cov,
                })
    return metrics


TIER_COLORS = {
    "T1": "#2ecc71",
    "T2": "#1abc9c",
    "T3": "#f1c40f",
    "T4": "#e67e22",
    "T5": "#e74c3c",
}

def fig_aris_heatmap_avg(metrics: dict, out_dir: str):
    mat = np.array([
        [np.mean([m["aris_score"] for m in metrics[r][j]]) for j in DOMAINS]
        for r in DOMAINS
    ])

    fig, ax = plt.subplots(figsize=(7, 6))
    im = ax.imshow(mat, cmap="YlGn", vmin=0, vmax=1, aspect="auto")
    plt.colorbar(im, ax=ax, label="Mean ArisScore")

    ax.set_xticks(range(len(DOMAINS)))
    ax.set_yticks(range(len(DOMAINS)))
    ax.set_xticklabels([DOMAIN_LABELS[d] for d in DOMAINS], fontsize=9)
    ax.set_yticklabels([DOMAIN_LABELS[d] for d in DOMAINS], fontsize=9)
    ax.set_xlabel("Job Posting Domain", fontsize=10, labelpad=8)
    ax.set_ylabel("Resume Domain", fontsize=10, labelpad=8)
    ax.set_title("Average ArisScore — Bulk Domain Matrix\n(n=50 resumes, 25 jobs, 1250 matches)", fontsize=11, pad=12)

    for i in range(len(DOMAINS)):
        for j in range(len(DOMAINS)):
            val = mat[i, j]
            color = "white" if val < 0.45 else "black"
            weight = "bold" if i == j else "normal"
            ax.text(j, i, f"{val:.3f}", ha="center", va="center",
                    fontsize=8.5, color=color, fontweight=weight)

    for k in range(len(DOMAINS)):
        ax.add_patch(plt.Rectangle((k - 0.5, k - 0.5), 1, 1,
                                   fill=False, edgecolor="#2c3e50", linewidth=2.5))

    plt.tight_layout()
    path = os.path.join(out_dir, "fig1_bulk_aris_heatmap.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def fig_graph_sdr_scatter_diagonal(metrics: dict, out_dir: str):
    fig, ax = plt.subplots(figsize=(8, 5))
    
    x_positions = []
    y_values = []
    labels = []
    
    for i, d in enumerate(DOMAINS):
        sdrs = [m["graph_sdr"] for m in metrics[d][d]]
        y_values.extend(sdrs)
        x_positions.extend([i] * len(sdrs))
        labels.append(DOMAIN_LABELS[d])

    jitter = np.random.normal(0, 0.1, len(y_values))
    ax.scatter(np.array(x_positions) + jitter, y_values,
               alpha=0.6, edgecolors='none', s=25, color="#2980b9")

    for i, d in enumerate(DOMAINS):
        mean_val = np.mean([m["graph_sdr"] for m in metrics[d][d]])
        ax.hlines(mean_val, i-0.3, i+0.3, colors="#c0392b", linewidth=2, zorder=3)
        ax.text(i, mean_val + 0.02, f"{mean_val:.1%}", ha="center", fontweight="bold", color="#c0392b")

    ax.axhline(HYPOTHESIS_THRESHOLD, color="#c0392b", linewidth=1.5,
               linestyle="--", label=f"Hypothesis threshold ({int(HYPOTHESIS_THRESHOLD*100)}%)")

    ax.set_xticks(range(len(DOMAINS)))
    ax.set_xticklabels(labels)
    ax.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))
    ax.set_ylabel("Graph-Only Structural Discovery Rate")
    ax.set_title("Graph Traversal Discovery — All Diagonal Pairs (n=250 matches)\n"
                 "(Points show individual Resume-Job matches within same domain)", fontsize=10, pad=10)
    ax.legend(loc="upper right", fontsize=9)
    ax.spines[["top", "right"]].set_visible(False)
    ax.set_ylim(0, 0.7)

    plt.tight_layout()
    path = os.path.join(out_dir, "fig2_bulk_graph_sdr_diagonal.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def fig_tier_composition_avg(metrics: dict, out_dir: str):
    labels = [DOMAIN_LABELS[d] for d in DOMAINS]
    
    t_avg = { f"t{i}": [] for i in range(1, 6) }
    for d in DOMAINS:
        for i in range(1, 6):
            t_avg[f"t{i}"].append(np.mean([m[f"t{i}"] for m in metrics[d][d]]))

    x = np.arange(len(DOMAINS))
    width = 0.6

    fig, ax = plt.subplots(figsize=(8, 5))
    bottom = np.zeros(len(DOMAINS))
    for i in range(1, 6):
        tier = f"t{i}"
        label = f"Tier {i}"
        if i==1: label += " (Vector)"
        elif i==5: label += " (Hard Gap)"
        ax.bar(x, t_avg[tier], width, bottom=bottom, label=label, color=TIER_COLORS[tier.upper()])
        bottom += t_avg[tier]

    ax.set_xticks(x)
    ax.set_xticklabels(labels)
    ax.set_ylabel("Average Number of Skills")
    ax.set_title("Average Skill Coverage Tier Breakdown — Domain-Correct Pairs", fontsize=11, pad=10)
    ax.legend(loc="upper left", bbox_to_anchor=(1, 1), fontsize=9)
    ax.spines[["top", "right"]].set_visible(False)

    plt.tight_layout()
    path = os.path.join(out_dir, "fig3_bulk_tier_composition.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def fig_diagonal_vs_offdiagonal_box(metrics: dict, out_dir: str):
    diag_aris = []
    off_aris = []
    diag_sdr = []
    off_sdr = []

    for r in DOMAINS:
        for j in DOMAINS:
            aris_vals = [m["aris_score"] for m in metrics[r][j]]
            sdr_vals = [m["graph_sdr"] for m in metrics[r][j]]
            if r == j:
                diag_aris.extend(aris_vals)
                diag_sdr.extend(sdr_vals)
            else:
                off_aris.extend(aris_vals)
                off_sdr.extend(sdr_vals)

    fig, axes = plt.subplots(1, 2, figsize=(11, 5))

    def box(ax, diag, off, title, ylabel, threshold=None):
        bp = ax.boxplot(
            [diag, off],
            labels=[f"Same-Domain\n(n={len(diag)})", f"Cross-Domain\n(n={len(off)})"],
            patch_artist=True,
            showfliers=False,
            medianprops=dict(color="black", linewidth=2),
        )
        colors = ["#27ae60", "#aab7b8"]
        for patch, color in zip(bp["boxes"], colors):
            patch.set_facecolor(color)
            patch.set_alpha(0.8)

        if threshold is not None:
            ax.axhline(threshold, color="#c0392b", linewidth=1.4, linestyle="--",
                       label=f"Threshold ({threshold:.0%})")
            ax.legend(fontsize=8.5)

        ax.set_title(title, fontsize=10, pad=8)
        ax.set_ylabel(ylabel, fontsize=9.5)
        ax.spines[["top", "right"]].set_visible(False)

    box(axes[0], diag_aris, off_aris, "ArisScore Distribution", "ArisScore")
    box(axes[1], diag_sdr, off_sdr, "Graph SDR Distribution", "Graph SDR", threshold=HYPOTHESIS_THRESHOLD)
    axes[1].yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))

    plt.tight_layout()
    path = os.path.join(out_dir, "fig4_bulk_diagonal_vs_offdiagonal.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def fig_sdr_heatmap_avg(metrics: dict, out_dir: str):
    mat = np.array([
        [np.mean([m["graph_sdr"] for m in metrics[r][j]]) for j in DOMAINS]
        for r in DOMAINS
    ])

    fig, ax = plt.subplots(figsize=(7, 6))
    im = ax.imshow(mat, cmap="Blues", vmin=0, vmax=max(mat.max(), 0.35), aspect="auto")
    cbar = plt.colorbar(im, ax=ax)
    cbar.set_label("Mean Graph SDR")
    cbar.ax.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))

    ax.set_xticks(range(len(DOMAINS)))
    ax.set_yticks(range(len(DOMAINS)))
    ax.set_xticklabels([DOMAIN_LABELS[d] for d in DOMAINS], fontsize=9)
    ax.set_yticklabels([DOMAIN_LABELS[d] for d in DOMAINS], fontsize=9)
    ax.set_xlabel("Job Posting Domain", fontsize=10, labelpad=8)
    ax.set_ylabel("Resume Domain", fontsize=10, labelpad=8)
    ax.set_title("Average Graph SDR — Bulk Matrix", fontsize=11, pad=12)

    for i in range(len(DOMAINS)):
        for j in range(len(DOMAINS)):
            val = mat[i, j]
            text_color = "white" if val > 0.3 else "black"
            weight = "bold" if i == j else "normal"
            ax.text(j, i, f"{val:.1%}", ha="center", va="center",
                    fontsize=8.5, color=text_color, fontweight=weight)

    for k in range(len(DOMAINS)):
        ax.add_patch(plt.Rectangle((k - 0.5, k - 0.5), 1, 1,
                                   fill=False, edgecolor="#2c3e50", linewidth=2.5))

    plt.tight_layout()
    path = os.path.join(out_dir, "fig6_bulk_sdr_heatmap.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def fig_cumulative_coverage_avg(metrics: dict, out_dir: str):
    fig, ax = plt.subplots(figsize=(8, 5))

    colors_by_domain = {
        "Data Science":       "#3498db",
        "DevOps Engineering": "#9b59b6",
        "Sales":              "#27ae60",
        "Software Engineer":  "#e74c3c",
        "Trades":             "#e67e22",
    }

    x_labels = ["T1 only\n(vector)", "T1+T2\n(+implicit)", "T1+T2+T3\n(+prereq)", "T1+T2+T3+T4\n(+bridge)"]

    for d in DOMAINS:
        m_list = metrics[d][d]
        avg_cum = np.zeros(4)
        count = 0
        for m in m_list:
            total = m["total_job_skills"]
            if total == 0: continue
            avg_cum += np.array([
                m["t1"] / total,
                (m["t1"] + m["t2"]) / total,
                (m["t1"] + m["t2"] + m["t3"]) / total,
                (m["t1"] + m["t2"] + m["t3"] + m["t4"]) / total,
            ])
            count += 1
        
        if count > 0:
            avg_cum /= count
            label = d
            ax.plot(range(4), avg_cum, marker="o", linewidth=2,
                    color=colors_by_domain.get(d, "#333"), label=label, markersize=6)
            ax.annotate(f"{avg_cum[-1]:.0%}", xy=(3, avg_cum[-1]), xytext=(3.06, avg_cum[-1]),
                        fontsize=8.5, color=colors_by_domain.get(d, "#333"), va="center")

    ax.axhline(HYPOTHESIS_THRESHOLD, color="#c0392b", linewidth=1.4,
               linestyle="--", label=f"20% SDR threshold", alpha=0.8)

    ax.set_xticks(range(4))
    ax.set_xticklabels(x_labels, fontsize=9)
    ax.set_xlim(-0.2, 3.6)
    ax.set_ylim(0, 1.05)
    ax.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))
    ax.set_ylabel("Average Cumulative % of Skills Covered")
    ax.set_title("Average Cumulative Skill Coverage by Tier — Domain-Correct Pairs", fontsize=10, pad=10)
    ax.legend(fontsize=9, loc="upper left")
    ax.spines[["top", "right"]].set_visible(False)

    plt.tight_layout()
    path = os.path.join(out_dir, "fig5_bulk_cumulative_coverage.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def print_summary(metrics: dict):
    print("\n" + "="*65)
    print("  BULK EVALUATION HYPOTHESIS TEST")
    print("  'Graph traversal identifies ≥20% of domain-correct job skills'")
    print("="*65)

    print(f"\n{'Domain':<20} {'Matches':>8} {'Avg Aris':>10} {'Avg SDR':>10} {'% Pass':>8}")
    print("-"*65)

    total_pass = 0
    total_diagonal = 0
    
    for d in DOMAINS:
        m_list = metrics[d][d]
        sdrs = [m["graph_sdr"] for m in m_list]
        passes = sum(1 for s in sdrs if s >= HYPOTHESIS_THRESHOLD)
        avg_aris = np.mean([m["aris_score"] for m in m_list])
        avg_sdr = np.mean(sdrs)
        
        total_pass += passes
        total_diagonal += len(m_list)
        
        print(f"  {d:<18} {len(m_list):>8} {avg_aris:>10.3f} {avg_sdr:>9.1%} {passes/len(m_list):>7.0%}")

    print("-"*65)
    print(f"  TOTAL DIAGONAL: {total_diagonal} matches")
    print(f"  TOTAL PASSING:  {total_pass} ({total_pass/total_diagonal:.1%})")
    verdict = "SUPPORTED" if (total_pass/total_diagonal) >= 0.8 else "PARTIAL"
    print(f"  Verdict: {verdict} (at 80% success rate among diagonal matches)")
    print()


def _pipeline_skill_set(entry: dict) -> set:
    return set(
        entry.get("matchingSkills", []) +
        entry.get("implicitSkills", []) +
        entry.get("prereqSkills", entry.get("prereqMetSkills", [])) +
        entry.get("bridgeableSkills", [])
    )


def _bc_overlap(pb: dict, pc: dict) -> float:
    s1 = _pipeline_skill_set(pb)
    s2 = _pipeline_skill_set(pc)
    union = s1 | s2
    return len(s1 & s2) / len(union) if union else 0.0


def run_pipeline_eval(api: str, resume_ids: dict, job_ids: dict, out_dir: str, sample: int = 1) -> list:
    """
    Runs POST /api/eval/pipeline-compare for diagonal pairs.
    sample: how many resumes per domain (uses the first N uploaded per domain).
    Uses the first job uploaded per domain.
    """
    os.makedirs(out_dir, exist_ok=True)
    print(f"\n=== Pipeline Evaluation (A vs B vs C) — {sample} resume(s) × 1 job per domain ===")
    print(f"{'Domain':<22} {'A IDR':>8} {'B IDR':>8} {'C IDR':>8} {'B-C Ovlp':>9} {'C≥20%':>7}")
    print("-" * 65)

    all_results = []

    for domain in DOMAINS:
        r_ids = resume_ids[domain][:sample]
        j_id  = job_ids[domain][0]

        for r_id in r_ids:
            try:
                result = post(api, "/api/eval/pipeline-compare", {
                    "userProfileId": r_id,
                    "jobId": j_id,
                })
                pa = result.get("pipelineA", {})
                pb = result.get("pipelineB", {})
                pc = result.get("pipelineC", {})

                a_idr   = pa.get("implicitDiscoveryRate", 0)
                b_idr   = pb.get("implicitDiscoveryRate", 0)
                c_idr   = pc.get("implicitDiscoveryRate", 0)
                overlap = _bc_overlap(pb, pc)
                verdict = "PASS" if isinstance(c_idr, (int, float)) and c_idr >= HYPOTHESIS_THRESHOLD else "FAIL"

                entry = {
                    "domain": domain,
                    "resumeId": r_id,
                    "jobId": j_id,
                    "a_idr": a_idr,
                    "b_idr": b_idr,
                    "c_idr": c_idr,
                    "bc_overlap": overlap,
                    "pipelineA": pa,
                    "pipelineB": pb,
                    "pipelineC": pc,
                }
                all_results.append(entry)
                print(f"  {domain:<20} {a_idr:>8.4f} {b_idr:>8.4f} {c_idr:>8.4f} {overlap:>9.4f} {verdict:>7}")

            except Exception as e:
                print(f"  {domain:<20} ERROR: {e}")

    print("-" * 65)

    # Save raw
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    raw_path = os.path.join(out_dir, f"pipeline_results_{timestamp}.json")
    with open(raw_path, "w", encoding="utf-8") as f:
        json.dump(all_results, f, indent=2)
    print(f"\n  Pipeline results saved: {raw_path}")
    return all_results


def pipeline_domain_averages(results: list) -> dict:
    """Average IDR and overlap by domain."""
    agg = {d: {"a_idr": [], "b_idr": [], "c_idr": [], "bc_overlap": []} for d in DOMAINS}
    for r in results:
        d = r["domain"]
        if d in agg:
            agg[d]["a_idr"].append(r["a_idr"])
            agg[d]["b_idr"].append(r["b_idr"])
            agg[d]["c_idr"].append(r["c_idr"])
            agg[d]["bc_overlap"].append(r["bc_overlap"])
    return {d: {k: (np.mean(v) if v else 0.0) for k, v in vals.items()} for d, vals in agg.items()}


def _grounded_idr(grounding, raw_idr):
    if not isinstance(grounding, (int, float)) or not isinstance(raw_idr, (int, float)):
        return 0.0
    return min(raw_idr, 1.0) * grounding


def fig_pipeline_grounding(results: list, out_dir: str):
    agg = {d: {"A": [], "B": [], "C": []} for d in DOMAINS}
    for r in results:
        d = r["domain"]
        if d not in agg:
            continue
        agg[d]["A"].append(r.get("pipelineA", {}).get("groundingScore", 0) or 0)
        agg[d]["B"].append(r.get("pipelineB", {}).get("groundingScore", 0) or 0)
        agg[d]["C"].append(r.get("pipelineC", {}).get("groundingScore", 0) or 0)

    labels = [DOMAIN_LABELS[d] for d in DOMAINS]
    a_vals = [np.mean(agg[d]["A"]) if agg[d]["A"] else 0 for d in DOMAINS]
    b_vals = [np.mean(agg[d]["B"]) if agg[d]["B"] else 0 for d in DOMAINS]
    c_vals = [np.mean(agg[d]["C"]) if agg[d]["C"] else 0 for d in DOMAINS]

    x     = np.arange(len(DOMAINS))
    width = 0.25
    colors = {"A": "#e74c3c", "B": "#3498db", "C": "#27ae60"}

    fig, ax = plt.subplots(figsize=(9, 5))
    ax.bar(x - width, a_vals, width, label="Pipeline A (LLM Direct)", color=colors["A"], alpha=0.85)
    ax.bar(x,         b_vals, width, label="Pipeline B (Vector-RAG)", color=colors["B"], alpha=0.85)
    ax.bar(x + width, c_vals, width, label="Pipeline C (Graph-RAG)",  color=colors["C"], alpha=0.85)

    ax.set_xticks(x)
    ax.set_xticklabels(labels, fontsize=9)
    ax.set_ylim(0, 1.1)
    ax.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))
    ax.set_ylabel("Graph Grounding Score", fontsize=10)
    ax.set_title("Graph Grounding Score by Pipeline — Diagonal Pairs", fontsize=11, pad=10)
    ax.legend(fontsize=9)
    ax.spines[["top", "right"]].set_visible(False)

    plt.tight_layout()
    path = os.path.join(out_dir, "fig_pipeline_grounding.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def fig_pipeline_hallucination(results: list, out_dir: str):
    agg = {d: {"a_raw": [], "a_grnd": [], "b_raw": [], "b_grnd": [], "c_grnd": []} for d in DOMAINS}
    for r in results:
        d = r["domain"]
        if d not in agg:
            continue
        pa = r.get("pipelineA", {}); pb = r.get("pipelineB", {}); pc = r.get("pipelineC", {})
        agg[d]["a_raw"].append(pa.get("implicitDiscoveryRate", 0) or 0)
        agg[d]["b_raw"].append(pb.get("implicitDiscoveryRate", 0) or 0)
        agg[d]["a_grnd"].append(_grounded_idr(pa.get("groundingScore"), pa.get("implicitDiscoveryRate")))
        agg[d]["b_grnd"].append(_grounded_idr(pb.get("groundingScore"), pb.get("implicitDiscoveryRate")))
        agg[d]["c_grnd"].append(_grounded_idr(pc.get("groundingScore"), pc.get("implicitDiscoveryRate")))

    labels  = [DOMAIN_LABELS[d] for d in DOMAINS]
    a_raw   = [np.mean(agg[d]["a_raw"])  if agg[d]["a_raw"]  else 0 for d in DOMAINS]
    b_raw   = [np.mean(agg[d]["b_raw"])  if agg[d]["b_raw"]  else 0 for d in DOMAINS]
    a_grnd  = [np.mean(agg[d]["a_grnd"]) if agg[d]["a_grnd"] else 0 for d in DOMAINS]
    b_grnd  = [np.mean(agg[d]["b_grnd"]) if agg[d]["b_grnd"] else 0 for d in DOMAINS]
    c_grnd  = [np.mean(agg[d]["c_grnd"]) if agg[d]["c_grnd"] else 0 for d in DOMAINS]

    x      = np.arange(len(DOMAINS))
    bar_w  = 0.16
    colors = {"A": "#e74c3c", "B": "#3498db", "C": "#27ae60"}

    fig, ax = plt.subplots(figsize=(12, 5))
    ax.bar(x - 2*bar_w, a_raw,  bar_w, label="A Raw IDR",             color=colors["A"], alpha=0.4)
    ax.bar(x - 1*bar_w, a_grnd, bar_w, label="A Grounded IDR",        color=colors["A"])
    ax.bar(x + 0*bar_w, b_raw,  bar_w, label="B Raw IDR",             color=colors["B"], alpha=0.4)
    ax.bar(x + 1*bar_w, b_grnd, bar_w, label="B Grounded IDR",        color=colors["B"])
    ax.bar(x + 2*bar_w, c_grnd, bar_w, label="C Grounded IDR (ARIS)", color=colors["C"])
    ax.axhline(0.20, color="black", linestyle="--", linewidth=1, label="≥20% threshold")

    ax.set_xticks(x)
    ax.set_xticklabels(labels, fontsize=9)
    ax.set_ylim(0, max(max(a_raw + b_raw, default=1.0), 1.0) + 0.1)
    ax.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))
    ax.set_ylabel("Implicit Discovery Rate", fontsize=10)
    ax.set_title("Hallucination Effect: Raw vs Grounded IDR\n"
                 "(gap between faded and solid bars = unverified claims)", fontsize=11, pad=10)
    ax.legend(fontsize=8)
    ax.spines[["top", "right"]].set_visible(False)

    plt.tight_layout()
    path = os.path.join(out_dir, "fig_pipeline_hallucination.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def fig_pipeline_idr(avgs: dict, out_dir: str):
    labels   = [DOMAIN_LABELS[d] for d in DOMAINS]
    a_vals   = [avgs[d]["a_idr"]   for d in DOMAINS]
    b_vals   = [avgs[d]["b_idr"]   for d in DOMAINS]
    c_vals   = [avgs[d]["c_idr"] for d in DOMAINS]

    x     = np.arange(len(DOMAINS))
    width = 0.25

    fig, ax = plt.subplots(figsize=(9, 5))
    ax.bar(x - width, a_vals, width, label="Pipeline A (LLM Direct)", color="#e74c3c", alpha=0.85)
    ax.bar(x,         b_vals, width, label="Pipeline B (Vector-RAG)", color="#3498db", alpha=0.85)
    ax.bar(x + width, c_vals, width, label="Pipeline C (Graph-RAG)",  color="#27ae60", alpha=0.85)

    ax.axhline(HYPOTHESIS_THRESHOLD, color="#2c3e50", linewidth=1.4,
               linestyle="--", label="≥20% threshold")

    ax.set_xticks(x)
    ax.set_xticklabels(labels, fontsize=9)
    ax.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))
    ax.set_ylabel("Implicit Discovery Rate (IDR)", fontsize=10)
    ax.set_title("Implicit Discovery Rate by Pipeline — Diagonal Pairs", fontsize=11, pad=10)
    ax.legend(fontsize=9)
    ax.spines[["top", "right"]].set_visible(False)

    plt.tight_layout()
    path = os.path.join(out_dir, "fig_pipeline_idr.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def fig_pipeline_bc_overlap(avgs: dict, out_dir: str):
    labels = [DOMAIN_LABELS[d] for d in DOMAINS]
    vals   = [avgs[d]["bc_overlap"] for d in DOMAINS]
    colors = ["#27ae60" if v >= 0.5 else "#e67e22" for v in vals]

    fig, ax = plt.subplots(figsize=(7, 4.5))
    bars = ax.bar(labels, vals, color=colors, edgecolor="white", linewidth=0.8, width=0.55)

    ax.axhline(0.5, color="#2c3e50", linewidth=1.4, linestyle="--", label="50% agreement")

    for bar, val in zip(bars, vals):
        ax.text(bar.get_x() + bar.get_width() / 2, val + 0.01,
                f"{val:.0%}", ha="center", va="bottom", fontsize=9.5, fontweight="bold")

    ax.set_ylim(0, 1.05)
    ax.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))
    ax.set_ylabel("Jaccard Overlap Rate (B ∩ C / B ∪ C)", fontsize=10)
    ax.set_title("Pipeline B vs C Canonical Skill Agreement — Diagonal Pairs", fontsize=11, pad=10)
    ax.legend(fontsize=9)
    ax.spines[["top", "right"]].set_visible(False)

    plt.tight_layout()
    path = os.path.join(out_dir, "fig_pipeline_bc_overlap.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  Saved: {path}")


def save_raw_results(results: dict, metrics: dict, out_dir: str):
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    path = os.path.join(out_dir, f"bulk_results_{timestamp}.json")
    # results dict is very large; only metrics are written to disk
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"metrics": metrics}, f, indent=2)
    print(f"  Metrics saved: {path}")


def main():
    parser = argparse.ArgumentParser(description="ARIS Bulk Evaluation")
    parser.add_argument("--api", default=DEFAULT_API, help="API base URL")
    parser.add_argument("--skip-upload", action="store_true",
                        help="Skip upload phase and load IDs from uploaded_bulk_ids.json")
    parser.add_argument("--skip-pipeline", action="store_true",
                        help="Skip pipeline comparison phase")
    parser.add_argument("--pipeline-only", action="store_true",
                        help="Skip the traditional bulk eval and run only the pipeline phase")
    parser.add_argument("--pipeline-sample", type=int, default=1, metavar="N",
                        help="Number of resumes per domain to use for pipeline eval (default: 1)")
    args = parser.parse_args()

    api = args.api.rstrip("/")
    ids_path = os.path.join(RESULTS_DIR, "uploaded_bulk_ids.json")

    print(f"\nARIS Bulk Evaluation")
    print(f"API: {api}")
    print(f"Results:  {RESULTS_DIR}")
    print(f"Pipeline: {PIPELINE_RESULTS_DIR}")

    if args.skip_upload and os.path.exists(ids_path):
        print("\n[skip-upload] Loading IDs from previous run ...")
        with open(ids_path) as f:
            saved = json.load(f)
        resume_ids = saved["resume_ids"]
        job_ids    = saved["job_ids"]
    else:
        resume_ids = upload_resumes(api)
        job_ids    = upload_jobs(api)
        with open(ids_path, "w") as f:
            json.dump({"resume_ids": resume_ids, "job_ids": job_ids}, f, indent=2)
        print(f"\n  IDs saved to: {ids_path}")

    if not args.pipeline_only:
        results = run_matrix(api, resume_ids, job_ids)
        metrics = compute_metrics(results)

        print("\n=== Generating Bulk Figures ===")
        fig_aris_heatmap_avg(metrics, RESULTS_DIR)
        fig_graph_sdr_scatter_diagonal(metrics, RESULTS_DIR)
        fig_tier_composition_avg(metrics, RESULTS_DIR)
        fig_diagonal_vs_offdiagonal_box(metrics, RESULTS_DIR)
        fig_cumulative_coverage_avg(metrics, RESULTS_DIR)
        fig_sdr_heatmap_avg(metrics, RESULTS_DIR)

        print_summary(metrics)
        save_raw_results(results, metrics, RESULTS_DIR)

    if not args.skip_pipeline:
        pipeline_results = run_pipeline_eval(
            api, resume_ids, job_ids,
            out_dir=PIPELINE_RESULTS_DIR,
            sample=args.pipeline_sample,
        )

        if pipeline_results:
            avgs = pipeline_domain_averages(pipeline_results)
            print("\n=== Generating Pipeline Figures ===")
            fig_pipeline_grounding(pipeline_results, PIPELINE_RESULTS_DIR)
            fig_pipeline_hallucination(pipeline_results, PIPELINE_RESULTS_DIR)
            fig_pipeline_idr(avgs, PIPELINE_RESULTS_DIR)
            fig_pipeline_bc_overlap(avgs, PIPELINE_RESULTS_DIR)

    print("\nDone.")


if __name__ == "__main__":
    main()
