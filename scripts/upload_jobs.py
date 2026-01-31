import csv
import requests
import argparse
import os
from collections import defaultdict
from tqdm import tqdm
import urllib3
import random

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

parser = argparse.ArgumentParser(description="Bulk upload jobs from CSV to ARIS.")
parser.add_argument("--file", "-f", default="scripts/job_postings/clean_jobs.csv", help="Path to CSV file")
parser.add_argument("--url", "-u", default="https://localhost:7293/api/Job", help="API Endpoint URL")
parser.add_argument("--items_per_category", "-n", type=int, default=3, help="Number of jobs to take per category")
parser.add_argument("--max_jobs", "-m", type=int, default=100000, help="Total maximum number of jobs to upload")
args = parser.parse_args()

def upload_job(job_row, url):
    enriched_description = (
        f"Job Title: {job_row['title']}\n"
        f"Company: {job_row['company']}\n"
        f"Location: {job_row['location']}\n"
        f"Work Type: {job_row['work_type']}\n"
        f"Employment Type: {job_row['employment_type']}\n\n"
        f"Job Description:\n{job_row['description']}"
    )

    company_slug = "".join(x for x in job_row['company'] if x.isalnum()).lower()
    if not company_slug:
        company_slug = "unknown"
    recruiter_id = f"recruiter_{company_slug}"

    payload = {
        "description": enriched_description,
        "recruiterId": recruiter_id
    }

    try:
        response = requests.post(url, json=payload, verify=False)
        if response.status_code == 200:
            return True, f"Success: {response.json().get('jobId')}"
        else:
            return False, f"Failed ({response.status_code}): {response.text}"
    except Exception as e:
        return False, f"Error: {e}"

def main():
    if not os.path.exists(args.file):
        print(f"Error: File '{args.file}' not found.")
        return

    print(f"Reading CSV: {args.file}...")
    
    jobs_by_title = defaultdict(list)
    
    try:
        with open(args.file, 'r', encoding='utf-8', newline='') as f:
            reader = csv.DictReader(f)
            for row in reader:
                title = row['title'].strip()
                company = row['company'].strip()
                
                if not title or not company:
                    continue
                if set(title) == {'*'} or set(company) == {'*'}:
                    continue
                if title.startswith('***') or company.startswith('***'):
                    continue
                    
                jobs_by_title[title].append(row)
    except Exception as e:
        print(f"Error reading CSV: {e}")
        return

    all_categories = list(jobs_by_title.keys())
    all_categories.sort() 
    
    print(f"Found {len(all_categories)} unique job titles.")
    print(f"Selecting {args.items_per_category} job(s) from EACH title (Max total: {args.max_jobs}).")
    
    jobs_to_upload = []
    
    for title in all_categories:
        if len(jobs_to_upload) >= args.max_jobs:
            print(f"Reached max_jobs limit ({args.max_jobs}). Stopping selection.")
            break
            
        available_jobs = jobs_by_title[title]
        selection = available_jobs[:args.items_per_category]
        jobs_to_upload.extend(selection)

    print(f"\nStarting upload of {len(jobs_to_upload)} jobs to {args.url}...")
    
    success_count = 0
    fail_count = 0
    
    for job in tqdm(jobs_to_upload):
        success, msg = upload_job(job, args.url)
        if success:
            success_count += 1
        else:
            fail_count += 1
            
    print(f"\nUpload Complete.")
    print(f"Successful: {success_count}")
    print(f"Failed:     {fail_count}")

if __name__ == "__main__":
    main()
