import os
import requests
import argparse
from tqdm import tqdm
import urllib3

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

parser = argparse.ArgumentParser(description="Bulk upload resumes to ARIS API.")
parser.add_argument("--input", "-i", default="cleaned_resumes", help="Input folder containing PDF resumes")
parser.add_argument("--url", "-u", default="https://localhost:7293/api/Resume/upload", help="API Endpoint URL")
args = parser.parse_args()

def upload_resume(file_path, url, index):
    filename = os.path.basename(file_path)
    user_id = f"dataset_{index}"
    
    try:
        with open(file_path, 'rb') as f:
            files = {'File': (filename, f, 'application/pdf')}
            data = {'UserId': user_id}
            
            response = requests.post(url, files=files, data=data, verify=False)
            
            if response.status_code == 200:
                return True, f"Success: {response.json()}"
            else:
                return False, f"Failed ({response.status_code}): {response.text}"
    except Exception as e:
        return False, f"Error: {e}"

def main():
    if not os.path.exists(args.input):
        print(f"Error: Input directory '{args.input}' not found.")
        return

    files = []
    for root, dirs, filenames in os.walk(args.input):
        for filename in filenames:
            if filename.lower().endswith(".pdf"):
                files.append(os.path.join(root, filename))
    
    files.sort()
    
    if not files:
        print(f"No PDF files found in '{args.input}'.")
        return

    print(f"Found {len(files)} resumes. Uploading to {args.url}...")
    
    success_count = 0
    fail_count = 0

    pbar = tqdm(enumerate(files, start=1), total=len(files))
    for i, file_path in pbar:
        success, msg = upload_resume(file_path, args.url, i)
        
        if success:
            success_count += 1
        else:
            fail_count += 1
            with open("upload_errors.log", "a") as log:
                log.write(f"{filename}: {msg}\n")
        
        pbar.set_description(f"Success: {success_count} | Fail: {fail_count}")

    print(f"\nUpload Complete.")
    print(f"Successful: {success_count}")
    print(f"Failed:     {fail_count}")
    if fail_count > 0:
        print("See 'upload_errors.log' for details.")

if __name__ == "__main__":
    main()
