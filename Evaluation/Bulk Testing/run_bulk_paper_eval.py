"""
ARIS — Bulk Paper-Aligned Three-Way Evaluation.
Scales run_paper_eval.py to all domain-correct pairs: 5 domains × 10 resumes × 5 jobs = 250 pairs.
Conditions: Original, ARIS GraphRAG, ChatGPT. Metrics follow Jagadeesh et al. (CLiC-it 2025).

Usage:
  python run_bulk_paper_eval.py
  python run_bulk_paper_eval.py --api http://localhost:5000
  python run_bulk_paper_eval.py --uuids path/to/uploaded_uuids.json
  python run_bulk_paper_eval.py --limit 2   (max pairs per domain, for quick testing)
"""

import os, sys, json, time, csv, argparse
import requests
import matplotlib.pyplot as plt
import matplotlib.ticker as mticker
import numpy as np
from datetime import datetime

BASE_URL        = "http://localhost:5000"
REQUEST_TIMEOUT = 600  # three-way-compare typically takes 15-60s per pair

SCRIPT_DIR  = os.path.dirname(os.path.abspath(__file__))
RESULTS_DIR = os.path.join(SCRIPT_DIR, "Results", "bulk_paper_eval")
os.makedirs(RESULTS_DIR, exist_ok=True)

DEFAULT_UUIDS_FILE = os.path.join(SCRIPT_DIR, "Results", "uploaded_bulk_ids.json")

DOMAINS = ["BackendPython", "DataEngineer", "DotNET", "DevOps", "Frontend"]

DOMAIN_LABELS = {
    "BackendPython": "Backend Python",
    "DataEngineer":  "Data Engineer",
    "DotNET":        "DotNET",
    "DevOps":        "DevOps",
    "Frontend":      "Frontend",
}

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

def safe_float(v) -> float:
    if isinstance(v, (int, float)):
        return float(v)
    return 0.0

def safe_int(v) -> int:
    if isinstance(v, (int, float)):
        return int(v)
    return 0

def load_uuids(path: str) -> dict:
    with open(path, encoding="utf-8") as f:
        return json.load(f)

def build_pairs(uuids: dict, domain: str) -> list[tuple[str, str, str, str]]:
    """Returns (resume_stem, resume_id, job_stem, job_id) for every pair in one domain."""
    resume_list = uuids.get("resume_ids", {}).get(domain, [])
    job_list    = uuids.get("job_ids",    {}).get(domain, [])
    pairs = [
        (f"r{ri+1:02d}", r_id, f"j{ji+1:02d}", j_id)
        for ri, r_id in enumerate(resume_list)
        for ji, j_id in enumerate(job_list)
    ]
    return pairs

def call_three_way_compare(api_base: str, resume_id: str, job_id: str) -> dict:
    """POST /api/eval/three-way-compare with one retry on HTTP 500."""
    url     = api_base.rstrip("/") + "/api/eval/three-way-compare"
    payload = {"resumeId": resume_id, "jobId": job_id}

    for attempt in range(1, 3):  # max 2 attempts
        try:
            r = requests.post(url, json=payload, timeout=REQUEST_TIMEOUT)
            r.raise_for_status()
            return r.json()
        except requests.HTTPError as e:
            if r.status_code == 500 and attempt == 1:
                print(f"    [HTTP 500 — retrying once]", flush=True)
                time.sleep(3)
                continue
            raise RuntimeError(f"HTTP {r.status_code}: {e}") from e
        except Exception as e:
            if attempt == 1:
                print(f"    [error on attempt 1 — retrying: {e}]", flush=True)
                time.sleep(3)
                continue
            raise RuntimeError(str(e)) from e

    raise RuntimeError("Exhausted retries")  # unreachable; satisfies type checker

def run_eval(api_base: str, uuids: dict, limit_per_domain: int | None = None) -> list[dict]:
    records      = []
    failed_pairs = []

    total_domains = len(DOMAINS)
    total_pairs   = sum(
        min(len(uuids.get("resumes", {}).get(d, {})) *
            len(uuids.get("jobs",    {}).get(d, {})), limit_per_domain or 9999)
        for d in DOMAINS
        if d in uuids.get("resumes", {}) and d in uuids.get("jobs", {})
    )

    print()
    print("=" * 95)
    print(f"  ARIS Bulk Paper Eval — Three-Way Compare  ({total_pairs} domain-correct pairs)")
    print("=" * 95)

    overall_idx = 0

    for domain in DOMAINS:
        label = DOMAIN_LABELS.get(domain, domain)
        pairs = build_pairs(uuids, domain)
        if limit_per_domain:
            pairs = pairs[:limit_per_domain]

        domain_total = len(pairs)
        if domain_total == 0:
            print(f"\n  [{label}] No pairs found — skipping")
            continue

        print(f"\n  [{label}]  {domain_total} pairs")

        for pair_idx, (r_stem, r_id, j_stem, j_id) in enumerate(pairs, 1):
            overall_idx += 1
            print(
                f"    [{label}] pair {pair_idx}/{domain_total}  "
                f"(overall {overall_idx}/{total_pairs})  "
                f"{r_stem[:30]} x {j_stem[:35]}",
                end=" ... ", flush=True
            )

            t0 = time.time()
            try:
                result  = call_three_way_compare(api_base, r_id, j_id)
                elapsed = time.time() - t0

                row = {
                    "domain":       domain,
                    "domainLabel":  label,
                    "resumeId":     r_id,
                    "jobId":        j_id,
                    "resumeFile":   r_stem,
                    "jobFile":      j_stem,
                    "jobTitle":     result.get("jobTitle", ""),
                    "chatGptModel": result.get("chatGptModel", ""),
                    "elapsedSec":   round(elapsed, 1),
                    "error":        None,
                }

                row["identifiedSkills"] = result.get("identifiedSkills", [])

                for ck in CONDITIONS:
                    c = result.get(ck, {})
                    row[ck] = {
                        "semanticSimilarityScore": safe_float(c.get("semanticSimilarityScore")),
                        "keywordMatchScore":        safe_float(c.get("keywordMatchScore")),
                        "atsScore":                 safe_float(c.get("atsScore")),
                        "matchingTermsCount":       safe_int(c.get("matchingTermsCount")),
                        "missingTermsCount":        safe_int(c.get("missingTermsCount")),
                        "hallucinationCount":       safe_int(c.get("hallucinationCount")),
                        "latencyMs":                safe_int(c.get("latencyMs")),
                        "matchingTermsDetail":      c.get("matchingTerms", []),
                        "missingTermsDetail":       c.get("missingTerms", []),
                        "newTermsAddedDetail":      c.get("newTermsAdded", []),
                    }

                records.append(row)
                print(
                    f"Done ({elapsed:.1f}s)  "
                    f"ATS orig={row['original']['atsScore']:.3f}  "
                    f"aris={row['arisGraphRag']['atsScore']:.3f}  "
                    f"gpt={row['chatGptBaseline']['atsScore']:.3f}"
                )

            except Exception as e:
                elapsed = time.time() - t0
                err_str = str(e)
                print(f"[FAILED: {err_str}]")
                failed_pairs.append({
                    "domain": domain, "resumeFile": r_stem, "jobFile": j_stem,
                    "resumeId": r_id, "jobId": j_id, "error": err_str
                })
                records.append({
                    "domain": domain, "domainLabel": label,
                    "resumeId": r_id, "jobId": j_id,
                    "resumeFile": r_stem, "jobFile": j_stem,
                    "elapsedSec": round(elapsed, 1), "error": err_str
                })

    if failed_pairs:
        print(f"\n  {len(failed_pairs)} pairs failed:")
        for fp in failed_pairs:
            print(f"    {fp['domain']} / {fp['resumeFile']} x {fp['jobFile']}: {fp['error']}")
    else:
        print(f"\n  All {overall_idx} pairs completed successfully.")

    return records

def aggregate(records: list[dict]) -> dict:
    """Returns per-domain and overall means for each condition, keyed by domain (plus '_overall')."""
    valid = [r for r in records if not r.get("error")]
    if not valid:
        return {}

    domain_buckets: dict[str, dict[str, list]] = {}
    for r in valid:
        d = r["domain"]
        if d not in domain_buckets:
            domain_buckets[d] = {ck: {
                "sem": [], "kw": [], "ats": [], "match": [], "miss": [], "hall": [], "lat": []
            } for ck in CONDITIONS}
        for ck in CONDITIONS:
            c = r[ck]
            domain_buckets[d][ck]["sem"].append(c["semanticSimilarityScore"])
            domain_buckets[d][ck]["kw"].append(c["keywordMatchScore"])
            domain_buckets[d][ck]["ats"].append(c["atsScore"])
            domain_buckets[d][ck]["match"].append(c["matchingTermsCount"])
            domain_buckets[d][ck]["miss"].append(c["missingTermsCount"])
            domain_buckets[d][ck]["hall"].append(c["hallucinationCount"])
            domain_buckets[d][ck]["lat"].append(c["latencyMs"])

    def mean(lst):
        return sum(lst) / len(lst) if lst else 0.0

    result = {}
    for d, cond_data in domain_buckets.items():
        result[d] = {
            "n": sum(1 for r in valid if r["domain"] == d)
        }
        for ck in CONDITIONS:
            b = cond_data[ck]
            result[d][ck] = {
                "meanSemanticSimilarityScore": mean(b["sem"]),
                "meanKeywordMatchScore":       mean(b["kw"]),
                "meanAtsScore":                mean(b["ats"]),
                "meanMatchingTermsCount":      mean(b["match"]),
                "meanMissingTermsCount":       mean(b["miss"]),
                "meanHallucinationCount":      mean(b["hall"]),
                "meanLatencyMs":               mean(b["lat"]),
            }

    result["_overall"] = {"n": len(valid)}
    for ck in CONDITIONS:
        result["_overall"][ck] = {
            "meanSemanticSimilarityScore": mean([r[ck]["semanticSimilarityScore"] for r in valid]),
            "meanKeywordMatchScore":       mean([r[ck]["keywordMatchScore"]        for r in valid]),
            "meanAtsScore":                mean([r[ck]["atsScore"]                 for r in valid]),
            "meanMatchingTermsCount":      mean([r[ck]["matchingTermsCount"]       for r in valid]),
            "meanMissingTermsCount":       mean([r[ck]["missingTermsCount"]        for r in valid]),
            "meanHallucinationCount":      mean([r[ck]["hallucinationCount"]       for r in valid]),
            "meanLatencyMs":               mean([r[ck]["latencyMs"]                for r in valid]),
        }

    return result

def print_metric_table(agg: dict, metric_key: str, label: str, fmt: str = ".4f"):
    valid_domains = [d for d in DOMAINS if d in agg]
    orig_label = CONDITION_LABELS["original"]
    aris_label = CONDITION_LABELS["arisGraphRag"]
    gpt_label  = CONDITION_LABELS["chatGptBaseline"]

    col_w = 12
    header = (
        f"  {'Domain':<18}  {'n':>4}  "
        f"{orig_label:>{col_w}}  {aris_label:>{col_w}}  {gpt_label:>{col_w}}  "
        f"{'ARIS-GPT diff':>13}"
    )
    sep = "  " + "-" * (len(header) - 2)
    print(f"\n  {label}")
    print(sep)
    print(header)
    print(sep)

    for d in valid_domains:
        dlab  = DOMAIN_LABELS.get(d, d)
        n     = agg[d]["n"]
        o_val = agg[d]["original"][metric_key]
        a_val = agg[d]["arisGraphRag"][metric_key]
        g_val = agg[d]["chatGptBaseline"][metric_key]
        diff  = a_val - g_val
        print(
            f"  {dlab:<18}  {n:>4}  "
            f"{o_val:>{col_w}{fmt}}  {a_val:>{col_w}{fmt}}  {g_val:>{col_w}{fmt}}  "
            f"{diff:>+13{fmt}}"
        )

    print(sep)
    n_tot = agg["_overall"]["n"]
    o_ov  = agg["_overall"]["original"][metric_key]
    a_ov  = agg["_overall"]["arisGraphRag"][metric_key]
    g_ov  = agg["_overall"]["chatGptBaseline"][metric_key]
    diff_ov = a_ov - g_ov
    print(
        f"  {'Overall':<18}  {n_tot:>4}  "
        f"{o_ov:>{col_w}{fmt}}  {a_ov:>{col_w}{fmt}}  {g_ov:>{col_w}{fmt}}  "
        f"{diff_ov:>+13{fmt}}"
    )
    print(sep)


def print_all_tables(agg: dict):
    if not agg:
        print("  No valid results to display.")
        return

    print("\n" + "=" * 95)
    print("  BULK PAPER EVAL — Aggregate Results (Original vs ARIS GraphRAG vs ChatGPT)")
    print("=" * 95)

    print_metric_table(agg, "meanAtsScore",                "ATS Score (0.5 x Semantic + 0.5 x Keyword)")
    print_metric_table(agg, "meanSemanticSimilarityScore",  "Semantic Similarity Score")
    print_metric_table(agg, "meanKeywordMatchScore",         "Keyword Match Score")
    print_metric_table(agg, "meanHallucinationCount",        "Mean Hallucination Count (T5 hard-gap false claims)", fmt=".2f")
    print_metric_table(agg, "meanMatchingTermsCount",        "Mean Matching Terms Count", fmt=".1f")
    print_metric_table(agg, "meanMissingTermsCount",         "Mean Missing Terms Count", fmt=".1f")


def print_hypothesis_verdicts(agg: dict, records: list[dict]):
    if not agg or "_overall" not in agg:
        return

    valid = [r for r in records if not r.get("error")]
    if not valid:
        return

    ov = agg["_overall"]
    aris_mean_sem  = ov["arisGraphRag"]["meanSemanticSimilarityScore"]
    gpt_mean_sem   = ov["chatGptBaseline"]["meanSemanticSimilarityScore"]
    aris_mean_ats  = ov["arisGraphRag"]["meanAtsScore"]
    gpt_mean_ats   = ov["chatGptBaseline"]["meanAtsScore"]
    aris_mean_kw   = ov["arisGraphRag"]["meanKeywordMatchScore"]
    gpt_mean_kw    = ov["chatGptBaseline"]["meanKeywordMatchScore"]
    aris_mean_hall = ov["arisGraphRag"]["meanHallucinationCount"]
    gpt_mean_hall  = ov["chatGptBaseline"]["meanHallucinationCount"]

    n = len(valid)
    wins_sem  = sum(1 for r in valid if r["arisGraphRag"]["semanticSimilarityScore"] > r["chatGptBaseline"]["semanticSimilarityScore"])
    wins_ats  = sum(1 for r in valid if r["arisGraphRag"]["atsScore"]                > r["chatGptBaseline"]["atsScore"])
    wins_kw   = sum(1 for r in valid if r["arisGraphRag"]["keywordMatchScore"]       > r["chatGptBaseline"]["keywordMatchScore"])
    wins_hall = sum(1 for r in valid if r["arisGraphRag"]["hallucinationCount"]      <= r["chatGptBaseline"]["hallucinationCount"])

    sem_lift  = aris_mean_sem  - gpt_mean_sem
    ats_lift  = aris_mean_ats  - gpt_mean_ats
    kw_lift   = aris_mean_kw   - gpt_mean_kw
    hall_diff = aris_mean_hall - gpt_mean_hall

    print("\n" + "=" * 95)
    print("  HYPOTHESIS VERDICTS")
    print("=" * 95)
    print(
        f"  Claim 1  ARIS Semantic > GPT Semantic:  "
        f"{wins_sem}/{n} pairs win  mean lift {sem_lift:+.4f}  "
        f"=> {'PASS' if aris_mean_sem > gpt_mean_sem else 'FAIL'}"
    )
    print(
        f"  Claim 2  ARIS ATS > GPT ATS:            "
        f"{wins_ats}/{n} pairs win  mean lift {ats_lift:+.4f}  "
        f"=> {'PASS' if aris_mean_ats > gpt_mean_ats else 'FAIL'}"
    )
    print(
        f"  (bonus)  ARIS KW > GPT KW:              "
        f"{wins_kw}/{n} pairs win  mean lift {kw_lift:+.4f}  "
        f"=> {'PASS' if aris_mean_kw > gpt_mean_kw else 'FAIL'}"
    )
    print(
        f"  Claim 3  ARIS Hall <= GPT Hall:          "
        f"{wins_hall}/{n} pairs win  mean diff {hall_diff:+.2f}  "
        f"=> {'PASS' if aris_mean_hall <= gpt_mean_hall else 'FAIL'}"
    )
    print("=" * 95)

def save_csv(records: list[dict], out_dir: str):
    path = os.path.join(out_dir, "bulk_paper_eval_results.csv")
    fieldnames = [
        "domain", "resumeFile", "jobFile", "resumeId", "jobId", "jobTitle",
        "condition",
        "semanticSimilarityScore", "keywordMatchScore", "atsScore",
        "matchingTermsCount", "missingTermsCount", "hallucinationCount",
        "latencyMs",
    ]
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for r in records:
            if r.get("error"):
                continue
            for ck in CONDITIONS:
                c = r[ck]
                writer.writerow({
                    "domain":                   DOMAIN_LABELS.get(r["domain"], r["domain"]),
                    "resumeFile":               r.get("resumeFile", r["resumeId"]),
                    "jobFile":                  r.get("jobFile", r["jobId"]),
                    "resumeId":                 r["resumeId"],
                    "jobId":                    r["jobId"],
                    "jobTitle":                 r.get("jobTitle", ""),
                    "condition":                CONDITION_LABELS[ck],
                    "semanticSimilarityScore":  c["semanticSimilarityScore"],
                    "keywordMatchScore":        c["keywordMatchScore"],
                    "atsScore":                 c["atsScore"],
                    "matchingTermsCount":        c["matchingTermsCount"],
                    "missingTermsCount":         c["missingTermsCount"],
                    "hallucinationCount":        c["hallucinationCount"],
                    "latencyMs":                c["latencyMs"],
                })
    print(f"  CSV saved:     {path}")
    return path


def save_summary_json(records: list[dict], out_dir: str):
    ts   = datetime.now().strftime("%Y%m%d_%H%M%S")
    path = os.path.join(out_dir, "bulk_paper_eval_summary.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"generated_at": ts, "total_pairs": len(records), "records": records}, f, indent=2)
    print(f"  Summary JSON:  {path}")
    return path


def save_aggregate_json(agg: dict, out_dir: str):
    ts   = datetime.now().strftime("%Y%m%d_%H%M%S")
    path = os.path.join(out_dir, "bulk_paper_eval_aggregate.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"generated_at": ts, "aggregate": agg}, f, indent=2)
    print(f"  Aggregate JSON: {path}")
    return path

def generate_figures(agg: dict):
    valid_domains = [d for d in DOMAINS if d in agg]
    if not valid_domains:
        print("  No valid domains for figures.")
        return

    labels = [DOMAIN_LABELS.get(d, d) for d in valid_domains]
    x      = np.arange(len(valid_domains))
    width  = 0.25

    def bar_group(ax, metric_key, title, ylabel, fmt="%.4f", ylim=None):
        for ci, ck in enumerate(CONDITIONS):
            vals = [agg[d][ck][metric_key] for d in valid_domains]
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
    bar_group(ax, "meanSemanticSimilarityScore",
              f"Semantic Similarity Score: Original vs ARIS vs ChatGPT (n={agg['_overall']['n']} pairs)",
              "Mean Cosine Similarity to Job Description (Qwen3)", ylim=(0, 1.15))
    plt.tight_layout()
    p1 = os.path.join(RESULTS_DIR, "fig1_semantic_similarity.png")
    plt.savefig(p1, dpi=150); plt.close()
    print(f"  Saved: {p1}")

    fig, ax = plt.subplots(figsize=(11, 5))
    bar_group(ax, "meanAtsScore",
              f"ATS Score (0.5 × Semantic + 0.5 × Keyword): Original vs ARIS vs ChatGPT (n={agg['_overall']['n']} pairs)",
              "Mean ATS Score", ylim=(0, 1.15))
    plt.tight_layout()
    p2 = os.path.join(RESULTS_DIR, "fig2_ats_score.png")
    plt.savefig(p2, dpi=150); plt.close()
    print(f"  Saved: {p2}")

    fig, ax = plt.subplots(figsize=(11, 5))
    bar_group(ax, "meanMatchingTermsCount",
              f"Mean Matching Terms Count: Original vs ARIS vs ChatGPT (n={agg['_overall']['n']} pairs)",
              "Mean Job Skills Found in Resume Text", fmt="%.1f")
    ax.yaxis.set_major_locator(mticker.MaxNLocator(integer=True))
    plt.tight_layout()
    p3 = os.path.join(RESULTS_DIR, "fig3_matching_terms.png")
    plt.savefig(p3, dpi=150); plt.close()
    print(f"  Saved: {p3}")

    fig, ax = plt.subplots(figsize=(10, 5))
    w2 = 0.35
    aris_hall = [agg[d]["arisGraphRag"]["meanHallucinationCount"]      for d in valid_domains]
    gpt_hall  = [agg[d]["chatGptBaseline"]["meanHallucinationCount"]   for d in valid_domains]
    bars_a = ax.bar(x - w2/2, aris_hall, w2, label="ARIS GraphRAG", color=CONDITION_COLORS["arisGraphRag"],    alpha=0.85)
    bars_g = ax.bar(x + w2/2, gpt_hall,  w2, label="ChatGPT",       color=CONDITION_COLORS["chatGptBaseline"], alpha=0.85)
    ax.bar_label(bars_a, fmt="%.2f", padding=3, fontsize=9)
    ax.bar_label(bars_g, fmt="%.2f", padding=3, fontsize=9)
    ax.set_title(f"Mean Hallucination Count (T5 Hard-Gap Skills Claimed): ARIS vs ChatGPT (n={agg['_overall']['n']} pairs)", fontsize=11)
    ax.set_ylabel("Mean T5 Skills Falsely Claimed")
    ax.set_xticks(x)
    ax.set_xticklabels(labels, fontsize=9)
    ax.legend()
    plt.tight_layout()
    p4 = os.path.join(RESULTS_DIR, "fig4_hallucination_count.png")
    plt.savefig(p4, dpi=150); plt.close()
    print(f"  Saved: {p4}")


def main():
    parser = argparse.ArgumentParser(
        description="ARIS bulk paper-aligned three-way evaluation (250 domain-correct pairs)"
    )
    parser.add_argument("--api",   default=BASE_URL,
                        help=f"API base URL (default: {BASE_URL})")
    parser.add_argument("--uuids", default=DEFAULT_UUIDS_FILE,
                        help="Path to uploaded_uuids.json")
    parser.add_argument("--limit", type=int, default=None,
                        help="Max pairs per domain (for quick testing, e.g. --limit 2)")
    args = parser.parse_args()

    api_base = args.api.rstrip("/")

    print("=" * 65)
    print("  ARIS — Bulk Paper Eval (Three-Way Compare)")
    print("  Original vs ARIS GraphRAG vs ChatGPT")
    print("=" * 65)
    print(f"  API:     {api_base}")
    print(f"  UUIDs:   {args.uuids}")
    print(f"  Output:  {RESULTS_DIR}")
    if args.limit:
        print(f"  Limit:   {args.limit} pairs per domain (test mode)")

    if not os.path.exists(args.uuids):
        print(f"\nERROR: UUID file not found: {args.uuids}")
        sys.exit(1)
    uuids = load_uuids(args.uuids)

    missing = []
    for d in DOMAINS:
        has_r = bool(uuids.get("resume_ids", {}).get(d))
        has_j = bool(uuids.get("job_ids",    {}).get(d))
        if not has_r or not has_j:
            missing.append(d)
    if missing:
        print(f"\nERROR: Missing resume or job UUIDs for domains: {missing}")
        print("  Re-run the upload script to populate these domains, then retry.")
        sys.exit(1)

    records = run_eval(api_base, uuids, limit_per_domain=args.limit)

    valid_count = sum(1 for r in records if not r.get("error"))
    fail_count  = len(records) - valid_count
    print(f"\n  Evaluated: {valid_count} pairs OK, {fail_count} failed")

    agg = aggregate(records)

    print_all_tables(agg)
    print_hypothesis_verdicts(agg, records)

    print("\n=== Generating Figures ===")
    generate_figures(agg)

    print("\n=== Saving Outputs ===")
    save_csv(records, RESULTS_DIR)
    save_summary_json(records, RESULTS_DIR)
    save_aggregate_json(agg, RESULTS_DIR)

    print(f"\nDone. Results saved to: {RESULTS_DIR}")


if __name__ == "__main__":
    main()
