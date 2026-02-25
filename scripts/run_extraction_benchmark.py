#!/usr/bin/env python3
"""
run_extraction_benchmark.py
Runs the LLM vs SLM extraction benchmark against all gold standard job postings and resumes.
Results saved to Development/scripts/benchmark_results/<timestamp>_<type>_<name>.json

Usage:
    python run_extraction_benchmark.py
    python run_extraction_benchmark.py --runs 1
    python run_extraction_benchmark.py --runs 3 --api-base https://localhost:7293
"""

import argparse
import json
import re
import ssl
import urllib.request
import urllib.error
from datetime import datetime
from pathlib import Path

import pypdf

API_BASE = "https://localhost:7293"
ENDPOINT = "/api/eval/extraction-benchmark"
JOB_POSTINGS_DIR = r"C:\dev\Masters Capstone\Test Job Postings"
RESUMES_DIR = r"C:\dev\Masters Capstone\Test Resumes\Redacted"
RESULTS_DIR = Path(__file__).parent / "benchmark_results"
DEFAULT_MODELS = ["mistral", "llama3.2:3b"]
DEFAULT_RUNS = 3


def extract_pdf_text(path: Path) -> str:
    reader = pypdf.PdfReader(str(path))
    return "\n".join(page.extract_text() or "" for page in reader.pages).strip()


def post_json(url: str, payload: dict, ctx: ssl.SSLContext) -> dict:
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=data,
        headers={"Content-Type": "application/json; charset=utf-8"},
        method="POST",
    )
    with urllib.request.urlopen(req, context=ctx, timeout=600) as resp:
        return json.loads(resp.read().decode("utf-8"))


def safe_name(s: str) -> str:
    return re.sub(r"[^a-zA-Z0-9_\-]", "_", s)


def print_table_row(col_widths: list[int], values: list[str], color: str = ""):
    row = "".join(v.ljust(w) for v, w in zip(values, col_widths))
    print(row)


def main():
    parser = argparse.ArgumentParser(description="ARIS extraction benchmark runner")
    parser.add_argument("--runs", type=int, default=DEFAULT_RUNS)
    parser.add_argument("--api-base", default=API_BASE)
    parser.add_argument("--models", nargs="+", default=DEFAULT_MODELS)
    args = parser.parse_args()

    endpoint = args.api_base.rstrip("/") + ENDPOINT
    RESULTS_DIR.mkdir(exist_ok=True)
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")

    # Collect all fixtures: job postings (txt) + resumes (pdf)
    fixtures: list[tuple[Path, str]] = []  # (path, type)
    for p in sorted(Path(JOB_POSTINGS_DIR).glob("*.txt")):
        fixtures.append((p, "job"))
    for p in sorted(Path(RESUMES_DIR).glob("*.pdf")):
        fixtures.append((p, "resume"))

    if not fixtures:
        print(f"ERROR: No fixtures found in {JOB_POSTINGS_DIR} or {RESUMES_DIR}")
        return

    # SSL context — trust self-signed localhost dev cert
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE

    print()
    print("=== EXTRACTION BENCHMARK: Mistral 7B vs Qwen3:4b ===")
    print(f"Endpoint     : {endpoint}")
    print(f"Models       : {', '.join(args.models)}")
    print(f"Runs/fixture : {args.runs}")
    job_count = sum(1 for _, t in fixtures if t == "job")
    resume_count = sum(1 for _, t in fixtures if t == "resume")
    print(f"Fixtures     : {job_count} job postings, {resume_count} resumes")
    print()

    col_widths = [34, 12, 15, 9, 7]
    header = ["Fixture", "Model", "Avg Latency", "Skills", "Roles"]
    divider = "-" * sum(col_widths)
    print_table_row(col_widths, header)
    print(divider)

    all_latencies: dict[str, list[float]] = {m: [] for m in args.models}
    all_results = []

    for fixture_path, fixture_type in fixtures:
        fixture_name = fixture_path.stem

        if fixture_type == "resume":
            text = extract_pdf_text(fixture_path)
        else:
            text = fixture_path.read_text(encoding="utf-8")

        payload = {
            "text": text,
            "type": fixture_type,
            "runs": args.runs,
            "models": args.models,
        }

        label = f"{fixture_name} [{fixture_type}]"
        print(f"\nRunning: {label} ...")

        try:
            response = post_json(endpoint, payload, ctx)

            # Save result
            out_file = RESULTS_DIR / f"{timestamp}_{fixture_type}_{safe_name(fixture_name)}.json"
            out_file.write_text(json.dumps(response, indent=2, ensure_ascii=False), encoding="utf-8")
            print(f"  Saved: {out_file}")

            results = response.get("results", {})
            for model in args.models:
                r = results.get(model, {})
                if not r:
                    print_table_row(col_widths, [fixture_name, model, "N/A", "-", "-"])
                    continue

                avg_ms = r.get("avgLatencyMs", 0)
                skills = r.get("skillCount", "-")
                roles  = r.get("roleCount", "-")
                status = "" if r.get("success") else " [FAILED]"
                avg_str = f"{avg_ms:,.0f} ms"

                print_table_row(col_widths, [fixture_name, model, avg_str, str(skills), str(roles)])

                if r.get("success") and avg_ms > 0:
                    all_latencies[model].append(avg_ms)

            cmp = response.get("comparison", {})
            if cmp.get("fasterModel"):
                speedup  = cmp.get("speedupFactor", 1)
                shared   = len(cmp.get("sharedSkills", []))
                unique_lm = len(cmp.get("uniqueToLargerModel", []))
                print(f"  {'':>{sum(col_widths[:1])}}Speedup: {speedup:.1f}x (faster={cmp['fasterModel']})  "
                      f"Shared: {shared}  Unique to mistral: {unique_lm}")

            all_results.append({
                "fixture": fixture_name,
                "type": fixture_type,
                "response": response,
            })

        except urllib.error.HTTPError as e:
            body = e.read().decode("utf-8", errors="replace")
            print(f"  ERROR {e.code} for {label}: {body}")
        except Exception as e:
            print(f"  ERROR for {label}: {e}")

    print()
    print(divider)
    print()
    print("=== SUMMARY: Average Latency Across All Fixtures ===")
    for model in args.models:
        lats = all_latencies[model]
        if lats:
            avg = sum(lats) / len(lats)
            print(f"  {model:<16} avg: {avg:,.0f} ms  ({len(lats)} fixtures)")
        else:
            print(f"  {model:<16} no successful runs")

    print()
    print(f"Results saved to: {RESULTS_DIR}")
    print("Done.")


if __name__ == "__main__":
    main()
