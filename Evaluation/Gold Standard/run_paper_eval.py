"""
ARIS — Paper-Aligned Three-Way Evaluation (Jagadeesh et al., CLiC-it 2025).
Scores 5 diagonal pairs across three conditions: Original, ARIS GraphRAG, ChatGPT.

Usage:
  python run_paper_eval.py [--api https://your-api-url]
  python run_paper_eval.py [--fixture-uuids path/to/fixture_uuids.json]
"""

import os, sys, json, time, base64, argparse, csv
import requests
import matplotlib.pyplot as plt
import matplotlib.ticker as mticker
import numpy as np

SCRIPT_DIR  = os.path.dirname(os.path.abspath(__file__))
RESULTS_DIR = os.path.join(SCRIPT_DIR, "Results", "paper_eval")
PDF_DIR     = os.path.join(RESULTS_DIR, "PDFs")
os.makedirs(RESULTS_DIR, exist_ok=True)
os.makedirs(PDF_DIR, exist_ok=True)

DEFAULT_API = "http://localhost:5000"

_FIXTURE_KEY_MAP = {
    "backend_python": "Backend Python",
    "data_engineer":  "Data Engineer",
    "dotnet":         "DotNET",
    "devops":         "DevOps",
    "frontend":       "Frontend",
}

DOMAINS = list(_FIXTURE_KEY_MAP.values())

DEFAULT_FIXTURE_UUIDS = os.path.join(SCRIPT_DIR, "fixture_uuids.json")

CONDITIONS = ["original", "arisGraphRag", "chatGptBaseline"]
CONDITION_LABELS = {
    "original":        "Original",
    "arisGraphRag":    "ARIS GraphRAG",
    "chatGptBaseline": "ChatGPT",
}
CONDITION_COLORS = {
    "original":        "#95a5a6",
    "arisGraphRag":    "#27ae60",
    "chatGptBaseline": "#e67e22",
}

def load_fixture_uuids(path: str) -> tuple[dict, dict]:
    with open(path) as f:
        data = json.load(f)
    resume_ids = {}
    job_ids    = {}
    for raw_key, label in _FIXTURE_KEY_MAP.items():
        if "resumes" in data and raw_key in data["resumes"]:
            resume_ids[label] = data["resumes"][raw_key]
        if "jobs" in data and raw_key in data["jobs"]:
            job_ids[label] = data["jobs"][raw_key]
    return resume_ids, job_ids

def post(api_base: str, path: str, payload: dict, timeout: int = 600) -> dict:
    url = api_base.rstrip("/") + path
    r = requests.post(url, json=payload, timeout=timeout)
    r.raise_for_status()
    return r.json()

def safe_float(v) -> float:
    if isinstance(v, (int, float)):
        return float(v)
    return 0.0

def safe_int(v) -> int:
    if isinstance(v, (int, float)):
        return int(v)
    return 0

def save_pdf(cond_data: dict, condition_key: str, domain: str):
    b64 = cond_data.get("pdfBase64", "")
    if not b64:
        return
    fname = f"TailoredResume_{domain.replace(' ', '_')}_{condition_key}.pdf"
    path  = os.path.join(PDF_DIR, fname)
    with open(path, "wb") as f:
        f.write(base64.b64decode(b64))
    print(f"    PDF saved: {os.path.basename(path)}")

def run_eval(api_base: str, fixture_path: str) -> list[dict]:
    resume_ids, job_ids = load_fixture_uuids(fixture_path)

    missing = [d for d in DOMAINS if d not in resume_ids or d not in job_ids]
    if missing:
        print(f"ERROR: missing fixture UUIDs for: {missing}")
        sys.exit(1)

    rows = []

    print("\n" + "=" * 90)
    print("  ARIS Paper Eval — Three-Way Compare (Original vs ARIS vs ChatGPT) — 5 pairs")
    print("=" * 90)

    for domain in DOMAINS:
        r_id = resume_ids[domain]
        j_id = job_ids[domain]
        print(f"\n  [{domain}]  resume={r_id[:8]}…  job={j_id[:8]}…")

        t0 = time.time()
        try:
            result = post(api_base, "/api/eval/three-way-compare",
                          {"resumeId": r_id, "jobId": j_id})
        except Exception as e:
            print(f"    ERROR: {e}")
            rows.append({"domain": domain, "error": str(e)})
            continue
        elapsed = time.time() - t0
        print(f"    Done in {elapsed:.1f}s  |  job: {result.get('jobTitle', '?')}  "
              f"|  model: {result.get('chatGptModel', '?')}")

        row = {"domain": domain, "jobTitle": result.get("jobTitle", ""), "chatGptModel": result.get("chatGptModel", "")}

        row["identifiedSkills"] = result.get("identifiedSkills", [])

        for ck in CONDITIONS:
            c = result.get(ck, {})
            row[ck] = {
                "semanticSimilarityScore": safe_float(c.get("semanticSimilarityScore")),
                "keywordMatchScore":       safe_float(c.get("keywordMatchScore")),
                "atsScore":                safe_float(c.get("atsScore")),
                "matchingTermsCount":      safe_int(c.get("matchingTermsCount")),
                "missingTermsCount":       safe_int(c.get("missingTermsCount")),
                "hallucinationCount":      safe_int(c.get("hallucinationCount")),
                "newTermsAdded":           c.get("newTermsAdded", []),
                "latencyMs":               safe_int(c.get("latencyMs")),
                "summaryText":             c.get("summaryText", ""),
                "tailoredFullText":        c.get("tailoredFullText", ""),
            }
            save_pdf(c, ck, domain)

        row["arisMatchingDetails"] = result.get("arisGraphRag", {}).get("matchingTerms", [])
        row["arisMissingDetails"]  = result.get("arisGraphRag", {}).get("missingTerms", [])
        row["arisNewTermsDetails"] = result.get("arisGraphRag", {}).get("newTermsAdded", [])
        row["gptMatchingDetails"]  = result.get("chatGptBaseline", {}).get("matchingTerms", [])
        row["gptMissingDetails"]   = result.get("chatGptBaseline", {}).get("missingTerms", [])

        rows.append(row)
        print(f"    Orig ATS={row['original']['atsScore']:.4f}  "
              f"ARIS ATS={row['arisGraphRag']['atsScore']:.4f}  "
              f"GPT ATS={row['chatGptBaseline']['atsScore']:.4f}")

        identified = row["identifiedSkills"]
        t1_t4 = [s for s in identified if s.get("tier") in ("T1", "T2", "T3", "T4")]
        t5    = [s for s in identified if s.get("tier") == "T5"]

        print(f"    Identified skills (T1-T4): {len(t1_t4)}")
        for s in t1_t4:
            canon = s.get("canonicalName", "")
            orig  = s.get("originalName")
            label = s.get("tierLabel", s.get("tier", ""))
            name_str = f"{canon} (orig: {orig})" if orig and orig != canon else canon
            print(f"      [{label}] {name_str}")

        print(f"    Hard gaps (T5): {len(t5)}")
        for s in t5:
            print(f"      [Hard Gap] {s.get('canonicalName', '')}")

        aris_matching = row["arisMatchingDetails"]
        if aris_matching:
            print(f"    ARIS matched (canonical / original / matchedOn):")
            for s in aris_matching:
                canon     = s.get("canonicalName", "")
                orig      = s.get("originalName")
                matched_on = s.get("matchedOn")
                parts = [canon]
                if orig and orig != canon:
                    parts.append(f"orig={orig}")
                if matched_on and matched_on != canon:
                    parts.append(f"matchedOn={matched_on}")
                print(f"      {' | '.join(parts)}")

        aris_new = row["arisNewTermsDetails"]
        if aris_new:
            print(f"    ARIS new terms added: {len(aris_new)}")
            for s in aris_new:
                canon = s.get("canonicalName", "")
                orig  = s.get("originalName")
                print(f"      {canon}" + (f" (orig: {orig})" if orig and orig != canon else ""))

    return rows

def print_table(rows: list[dict]):
    valid = [r for r in rows if "error" not in r]

    header = (
        f"  {'Domain':<18}  {'Orig Sem':>8}  {'ARIS Sem':>8}  {'GPT Sem':>8}"
        f"  {'Orig ATS':>8}  {'ARIS ATS':>8}  {'GPT ATS':>8}"
        f"  {'Orig KW':>7}  {'ARIS KW':>7}  {'GPT KW':>7}"
        f"  {'ARIS Hall':>9}  {'GPT Hall':>8}"
    )
    sep = "-" * len(header)

    print("\n" + "=" * len(header))
    print("  PAPER EVAL TABLE — Original vs ARIS GraphRAG vs ChatGPT")
    print("=" * len(header))
    print(header)
    print(sep)

    for row in rows:
        if "error" in row:
            print(f"  {row['domain']:<18}  ERROR: {row['error']}")
            continue
        o = row["original"]
        a = row["arisGraphRag"]
        g = row["chatGptBaseline"]
        print(
            f"  {row['domain']:<18}"
            f"  {o['semanticSimilarityScore']:>8.4f}  {a['semanticSimilarityScore']:>8.4f}  {g['semanticSimilarityScore']:>8.4f}"
            f"  {o['atsScore']:>8.4f}  {a['atsScore']:>8.4f}  {g['atsScore']:>8.4f}"
            f"  {o['keywordMatchScore']:>7.4f}  {a['keywordMatchScore']:>7.4f}  {g['keywordMatchScore']:>7.4f}"
            f"  {a['hallucinationCount']:>9}  {g['hallucinationCount']:>8}"
        )

    print(sep)

    if not valid:
        return

    means = {}
    for ck in CONDITIONS:
        means[ck] = {
            "sem":  sum(r[ck]["semanticSimilarityScore"] for r in valid) / len(valid),
            "kw":   sum(r[ck]["keywordMatchScore"]       for r in valid) / len(valid),
            "ats":  sum(r[ck]["atsScore"]                for r in valid) / len(valid),
            "hall": sum(r[ck]["hallucinationCount"]      for r in valid) / len(valid),
        }

    print(f"\n  {'Condition':<18}  {'Mean Sem':>8}  {'Mean KW':>7}  {'Mean ATS':>8}  {'Mean Hall':>9}")
    print("  " + "-" * 60)
    for ck in CONDITIONS:
        m = means[ck]
        print(f"  {CONDITION_LABELS[ck]:<18}  {m['sem']:>8.4f}  {m['kw']:>7.4f}  {m['ats']:>8.4f}  {m['hall']:>9.2f}")
    print("  " + "-" * 60)

    aris_wins_sem  = sum(1 for r in valid if r["arisGraphRag"]["semanticSimilarityScore"] > r["chatGptBaseline"]["semanticSimilarityScore"])
    aris_wins_ats  = sum(1 for r in valid if r["arisGraphRag"]["atsScore"]                > r["chatGptBaseline"]["atsScore"])
    aris_wins_hall = sum(1 for r in valid if r["arisGraphRag"]["hallucinationCount"]      <= r["chatGptBaseline"]["hallucinationCount"])

    sem_lift  = means["arisGraphRag"]["sem"]  - means["chatGptBaseline"]["sem"]
    ats_lift  = means["arisGraphRag"]["ats"]  - means["chatGptBaseline"]["ats"]
    hall_diff = means["arisGraphRag"]["hall"] - means["chatGptBaseline"]["hall"]

    print(f"\n  Claim 1 (ARIS Sem > GPT Sem):       {aris_wins_sem}/5 pairs  mean lift {sem_lift:+.4f}  "
          f"=> {'PASS' if means['arisGraphRag']['sem'] > means['chatGptBaseline']['sem'] else 'FAIL'}")
    print(f"  Claim 2 (ARIS ATS > GPT ATS):       {aris_wins_ats}/5 pairs  mean lift {ats_lift:+.4f}  "
          f"=> {'PASS' if means['arisGraphRag']['ats'] > means['chatGptBaseline']['ats'] else 'FAIL'}")
    print(f"  Claim 3 (ARIS Hall <= GPT Hall):     {aris_wins_hall}/5 pairs  mean diff {hall_diff:+.2f}  "
          f"=> {'PASS' if means['arisGraphRag']['hall'] <= means['chatGptBaseline']['hall'] else 'FAIL'}")


def generate_figures(rows: list[dict]):
    valid = [r for r in rows if "error" not in r]
    if not valid:
        print("  No valid rows for figures.")
        return

    labels = [r["domain"] for r in valid]
    x      = np.arange(len(valid))
    width  = 0.25

    def bar_group(ax, key, title, ylabel, fmt="%.4f", ylim=None):
        for ci, ck in enumerate(CONDITIONS):
            vals = [r[ck][key] for r in valid]
            bars = ax.bar(x + (ci - 1) * width, vals, width,
                          label=CONDITION_LABELS[ck],
                          color=CONDITION_COLORS[ck], alpha=0.85)
            ax.bar_label(bars, fmt=fmt, padding=2, fontsize=7)
        ax.set_title(title, fontsize=11)
        ax.set_ylabel(ylabel)
        if ylim:
            ax.set_ylim(*ylim)
        ax.set_xticks(x)
        ax.set_xticklabels(labels, fontsize=9)
        ax.legend(fontsize=8)

    fig, ax = plt.subplots(figsize=(11, 5))
    bar_group(ax, "semanticSimilarityScore",
              "Semantic Similarity Score: Original vs ARIS vs ChatGPT",
              "Cosine Similarity to Job Description (Qwen3 embeddings)",
              ylim=(0, 1.15))
    plt.tight_layout()
    p1 = os.path.join(RESULTS_DIR, "fig1_semantic_similarity.png")
    plt.savefig(p1, dpi=150); plt.close()
    print(f"  Saved: {p1}")

    fig, ax = plt.subplots(figsize=(11, 5))
    bar_group(ax, "atsScore",
              "ATS Score (0.5 × Semantic + 0.5 × Keyword): Original vs ARIS vs ChatGPT",
              "ATS Score",
              ylim=(0, 1.15))
    plt.tight_layout()
    p2 = os.path.join(RESULTS_DIR, "fig2_ats_score.png")
    plt.savefig(p2, dpi=150); plt.close()
    print(f"  Saved: {p2}")

    fig, ax = plt.subplots(figsize=(11, 5))
    bar_group(ax, "matchingTermsCount",
              "Matching Terms Count: Original vs ARIS vs ChatGPT",
              "Job Skills Found in Resume Text",
              fmt="%d")
    ax.yaxis.set_major_locator(mticker.MaxNLocator(integer=True))
    plt.tight_layout()
    p3 = os.path.join(RESULTS_DIR, "fig3_matching_terms.png")
    plt.savefig(p3, dpi=150); plt.close()
    print(f"  Saved: {p3}")

    fig, ax = plt.subplots(figsize=(10, 5))
    w2 = 0.35
    aris_hall = [r["arisGraphRag"]["hallucinationCount"]      for r in valid]
    gpt_hall  = [r["chatGptBaseline"]["hallucinationCount"]   for r in valid]
    bars_a = ax.bar(x - w2/2, aris_hall, w2, label="ARIS GraphRAG", color=CONDITION_COLORS["arisGraphRag"],    alpha=0.85)
    bars_g = ax.bar(x + w2/2, gpt_hall,  w2, label="ChatGPT",       color=CONDITION_COLORS["chatGptBaseline"], alpha=0.85)
    ax.bar_label(bars_a, padding=3, fontsize=9)
    ax.bar_label(bars_g, padding=3, fontsize=9)
    ax.set_title("Hallucination Count (T5 Hard-Gap Skills Claimed): ARIS vs ChatGPT", fontsize=11)
    ax.set_ylabel("T5 Skills Falsely Claimed")
    ax.set_xticks(x)
    ax.set_xticklabels(labels, fontsize=9)
    ax.yaxis.set_major_locator(mticker.MaxNLocator(integer=True))
    ax.legend()
    plt.tight_layout()
    p4 = os.path.join(RESULTS_DIR, "fig4_hallucination_count.png")
    plt.savefig(p4, dpi=150); plt.close()
    print(f"  Saved: {p4}")


def save_csv(rows: list[dict]):
    path = os.path.join(RESULTS_DIR, "paper_eval_results.csv")
    fieldnames = ["domain", "condition",
                  "semanticSimilarityScore", "keywordMatchScore", "atsScore",
                  "matchingTermsCount", "missingTermsCount", "hallucinationCount",
                  "latencyMs"]
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for row in rows:
            if "error" in row:
                continue
            for ck in CONDITIONS:
                c = row[ck]
                writer.writerow({
                    "domain":                   row["domain"],
                    "condition":                CONDITION_LABELS[ck],
                    "semanticSimilarityScore":  c["semanticSimilarityScore"],
                    "keywordMatchScore":        c["keywordMatchScore"],
                    "atsScore":                 c["atsScore"],
                    "matchingTermsCount":       c["matchingTermsCount"],
                    "missingTermsCount":        c["missingTermsCount"],
                    "hallucinationCount":       c["hallucinationCount"],
                    "latencyMs":                c["latencyMs"],
                })
    print(f"  CSV saved: {path}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--api", default=DEFAULT_API)
    parser.add_argument("--fixture-uuids", default=DEFAULT_FIXTURE_UUIDS)
    args = parser.parse_args()

    rows = run_eval(args.api, args.fixture_uuids)

    print_table(rows)

    print("\n=== Generating Figures ===")
    generate_figures(rows)

    save_csv(rows)

    json_path = os.path.join(RESULTS_DIR, "paper_eval_summary.json")
    with open(json_path, "w") as f:
        json.dump(rows, f, indent=2)
    print(f"  JSON saved: {json_path}")

    print(f"\nDone. Results in: {RESULTS_DIR}")


if __name__ == "__main__":
    main()
