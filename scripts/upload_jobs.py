import csv
import requests
import argparse
import os
import glob
from collections import defaultdict
from tqdm import tqdm
import urllib3
import random

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

parser = argparse.ArgumentParser(description="Bulk upload jobs from CSV or Text Files to ARIS.")
parser.add_argument("--input", "-i", default="Development/Datasets/Job Postings", help="Path to CSV file or Directory of txt files")
parser.add_argument("--url", "-u", default="https://localhost:7293/api/Job", help="API Endpoint URL")
parser.add_argument("--items_per_category", "-n", type=int, default=3, help="Number of jobs to take per category (CSV only)")
parser.add_argument("--max_jobs", "-m", type=int, default=100000, help="Total maximum number of jobs to upload")
args = parser.parse_args()

def upload_payload(description, recruiter_id, url):
    payload = {
        "description": description,
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

def process_csv(filepath):
    print(f"Reading CSV: {filepath}...")
    jobs_by_title = defaultdict(list)
    
    try:
        with open(filepath, 'r', encoding='utf-8', newline='') as f:
            reader = csv.DictReader(f)
            for row in reader:
                title = row.get('title', '').strip()
                company = row.get('company', '').strip()
                
                if not title or not company:
                    continue
                if set(title) == {'*'} or set(company) == {'*'}:
                    continue
                if title.startswith('***') or company.startswith('***'):
                    continue
                    
                jobs_by_title[title].append(row)
    except Exception as e:
        print(f"Error reading CSV: {e}")
        return []

    all_categories = list(jobs_by_title.keys())
    all_categories.sort() 
    
    print(f"Found {len(all_categories)} unique job titles.")
    print(f"Selecting {args.items_per_category} job(s) from EACH title (Max total: {args.max_jobs}).")
    
    jobs_to_upload = []
    
    for title in all_categories:
        if len(jobs_to_upload) >= args.max_jobs:
            break
            
        available_jobs = jobs_by_title[title]
        selection = available_jobs[:args.items_per_category]
        
        for job_row in selection:
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
            
            jobs_to_upload.append((enriched_description, recruiter_id))

    return jobs_to_upload

def process_directory(dirpath):
    print(f"Scanning directory: {dirpath}...")
    jobs_to_upload = []
    
    # Recursive walk
    # Expected structure: BaseDir/Category/JobFile.txt
    for root, dirs, files in os.walk(dirpath):
        for file in files:
            if file.lower().endswith(".txt"):
                full_path = os.path.join(root, file)
                
                # Get category from folder name
                # If root is .../Data Science, category is "Data Science"
                category = os.path.basename(root)
                
                # Sanitize category for recruiter ID
                category_slug = "".join(x for x in category if x.isalnum() or x == ' ').strip().replace(' ', '_').lower()
                recruiter_id = f"recruiter_{category_slug}"
                
                try:
                    with open(full_path, 'r', encoding='utf-8') as f:
                        content = f.read()
                        if content.strip():
                            jobs_to_upload.append((content, recruiter_id))
                except Exception as e:
                    print(f"Error reading file {full_path}: {e}")
                    
    return jobs_to_upload

def main():
    if not os.path.exists(args.input):
        print(f"Error: Input '{args.input}' not found.")
        return

    jobs_to_upload = []

    if os.path.isdir(args.input):
        jobs_to_upload = process_directory(args.input)
    elif os.path.isfile(args.input):
        if args.input.lower().endswith('.csv'):
            jobs_to_upload = process_csv(args.input)
        else:
            print("Unsupported file type. Please provide a CSV or a Directory.")
            return

    if not jobs_to_upload:
        print("No jobs found to upload.")
        return

    # Limit total jobs if needed
    if len(jobs_to_upload) > args.max_jobs:
        jobs_to_upload = jobs_to_upload[:args.max_jobs]

    print(f"\nStarting upload of {len(jobs_to_upload)} jobs to {args.url}...")
    
    success_count = 0
    fail_count = 0
    
    for description, recruiter_id in tqdm(jobs_to_upload):
        success, msg = upload_payload(description, recruiter_id, args.url)
        if success:
            success_count += 1
        else:
            fail_count += 1
            
    print(f"\nUpload Complete.")
    print(f"Successful: {success_count}")
    print(f"Failed:     {fail_count}")

if __name__ == "__main__":
    main()