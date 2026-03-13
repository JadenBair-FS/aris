#!/usr/bin/env python3
"""
upload_test_fixtures.py
Re-uploads all gold standard test fixtures (resumes + job postings) to the ARIS API
and saves the resulting UUIDs to fixture_uuids.json in this directory.
Run this after a Docker volume reset.

Usage:
    python upload_test_fixtures.py
    python upload_test_fixtures.py --api-base http://localhost:5000
"""

import argparse
import json
import ssl
import urllib.request
import urllib.error
from pathlib import Path

import pypdf

SCRIPT_DIR     = Path(__file__).parent
API_BASE       = "http://localhost:5000"
JOB_DIR        = SCRIPT_DIR / "New Documents" / "Job Postings"
RESUME_PDF_DIR = SCRIPT_DIR / "New Documents" / "PDFS"
RECRUITER_ID   = "thesis-eval-recruiter"

JOB_MAP = {
    "JobPosting_BackendPython": "backend_python",
    "JobPosting_DataEngineer":  "data_engineer",
    "JobPosting_DotNET":        "dotnet",
    "JobPosting_DevOps":        "devops",
    "JobPosting_Frontend":      "frontend",
}

RESUME_MAP = {
    "Resume_BackendPython": "backend_python",
    "Resume_DataEngineer":  "data_engineer",
    "Resume_DotNET":        "dotnet",
    "Resume_DevOps":        "devops",
    "Resume_Frontend":      "frontend",
}

DOMAIN_ORDER = ["backend_python", "data_engineer", "dotnet", "devops", "frontend"]


def _ssl_ctx():
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    return ctx


def post_json(url, payload, ctx, timeout=300):
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url, data=data,
                                  headers={"Content-Type": "application/json; charset=utf-8"},
                                  method="POST")
    with urllib.request.urlopen(req, context=ctx, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def extract_pdf_text(path: Path) -> str:
    reader = pypdf.PdfReader(str(path))
    return "\n".join(page.extract_text() or "" for page in reader.pages).strip()


def main():
    parser = argparse.ArgumentParser(description="Upload gold standard test fixtures to ARIS API")
    parser.add_argument("--api-base", default=API_BASE)
    args = parser.parse_args()

    ctx = _ssl_ctx()
    base = args.api_base.rstrip("/")

    new_jobs    = {}
    new_resumes = {}

    print()
    print("=" * 60)
    print("ARIS Gold Standard — Upload Test Fixtures")
    print("=" * 60)

    print()
    print("=== UPLOADING JOB POSTINGS ===")
    for txt_file in sorted(JOB_DIR.glob("*.txt")):
        stem = txt_file.stem
        label = JOB_MAP.get(stem)
        if label is None:
            print(f"  SKIP (no mapping): {stem}")
            continue

        text = txt_file.read_text(encoding="utf-8")
        print(f"  Uploading {label} ... ", end="", flush=True)
        try:
            resp = post_json(f"{base}/api/job", {"description": text, "recruiterId": RECRUITER_ID}, ctx)
            job_id = resp.get("jobId") or resp.get("id")
            new_jobs[label] = str(job_id)
            print(f"OK  {job_id}")
        except urllib.error.HTTPError as e:
            body = e.read().decode("utf-8", errors="replace")
            print(f"ERROR {e.code}: {body[:200]}")
        except Exception as e:
            print(f"ERROR: {e}")

    print()
    print("=== UPLOADING RESUMES ===")
    for pdf_file in sorted(RESUME_PDF_DIR.glob("*.pdf")):
        stem = pdf_file.stem
        label = RESUME_MAP.get(stem)
        if label is None:
            print(f"  SKIP (no mapping): {stem}")
            continue

        user_id = f"thesis-eval-{label}"
        print(f"  Uploading {label} (PDF) ... ", end="", flush=True)
        try:
            extracted = extract_pdf_text(pdf_file)
            if len(extracted) <= 100:
                print(f"\n    PDF text sparse ({len(extracted)} chars) ... ", end="", flush=True)
            resp = post_json(f"{base}/api/resume/upload-text",
                             {"content": extracted or "(no text extracted)", "userId": user_id}, ctx)
            profile_id = resp.get("id")
            new_resumes[label] = str(profile_id)
            print(f"OK  {profile_id}")
        except urllib.error.HTTPError as e:
            body = e.read().decode("utf-8", errors="replace")
            print(f"ERROR {e.code}: {body[:200]}")
        except Exception as e:
            print(f"ERROR: {e}")

    txt_resume_dir = SCRIPT_DIR / "New Documents"
    for txt_file in sorted(txt_resume_dir.glob("Resume_*.txt")):
        stem = txt_file.stem
        label = RESUME_MAP.get(stem)
        if label is None:
            print(f"  SKIP (no mapping): {stem}")
            continue
        if label in new_resumes:
            print(f"  SKIP (already uploaded): {stem}")
            continue

        user_id = f"thesis-eval-{label}"
        print(f"  Uploading {label} (TXT) ... ", end="", flush=True)
        try:
            content = txt_file.read_text(encoding="utf-8").strip()
            resp = post_json(f"{base}/api/resume/upload-text",
                             {"content": content, "userId": user_id}, ctx)
            profile_id = resp.get("id")
            new_resumes[label] = str(profile_id)
            print(f"OK  {profile_id}")
        except urllib.error.HTTPError as e:
            body = e.read().decode("utf-8", errors="replace")
            print(f"ERROR {e.code}: {body[:200]}")
        except Exception as e:
            print(f"ERROR: {e}")

    print()
    print("=== FIXTURE UUIDs ===")
    print()
    print("Jobs:")
    for label in DOMAIN_ORDER:
        uid = new_jobs.get(label, "UPLOAD_FAILED")
        print(f"  {label:<20} {uid}")
    print()
    print("Resumes:")
    for label in DOMAIN_ORDER:
        uid = new_resumes.get(label, "UPLOAD_FAILED")
        print(f"  {label:<20} {uid}")
    print()

    uuid_file = SCRIPT_DIR / "fixture_uuids.json"
    uuid_file.write_text(
        json.dumps({"resumes": new_resumes, "jobs": new_jobs}, indent=2),
        encoding="utf-8"
    )
    print(f"UUIDs saved to: {uuid_file}")


if __name__ == "__main__":
    main()
