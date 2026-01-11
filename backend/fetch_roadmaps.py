import os
import json
import re
import time
import urllib.request
import urllib.error

# Configuration
BASE_URL = "https://roadmap.sh"
ROADMAPPED_URL = f"{BASE_URL}/roadmaps"
OUTPUT_DIR = os.path.join(os.path.dirname(__file__), "data", "roadmaps")

# Ensure output directory exists
os.makedirs(OUTPUT_DIR, exist_ok=True)

def fetch_html(url):
    try:
        req = urllib.request.Request(
            url, 
            headers={'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36'}
        )
        with urllib.request.urlopen(req) as response:
            return response.read().decode('utf-8')
    except urllib.error.URLError as e:
        print(f"Error fetching {url}: {e}")
        return None

def extract_roadmap_slugs(html):
    pattern = r'href="/([a-zA-Z0-9-]+)"'
    matches = re.findall(pattern, html)
    
    # Filter out common pages that are not roadmaps
    ignored = {
        'roadmaps', 'best-practices', 'guides', 'videos', 'about', 
        'signup', 'login', 'terms', 'privacy', 'pdfs', 'compare',
        'subscribe', 'pricing', 'teams', 'advertising', 'brand'
    }
    
    # Also ignore assets or api calls
    valid_slugs = set()
    for slug in matches:
        if slug not in ignored and not slug.startswith('assets') and not slug.startswith('api'):
            valid_slugs.add(slug)
            
    return sorted(list(valid_slugs))

def fetch_and_save_json(slug):
    json_url = f"{BASE_URL}/{slug}.json"
    print(f"Fetching JSON for: {slug}...")
    
    content = fetch_html(json_url)
    if content:
        try:
            # Verify it's valid JSON
            json_data = json.loads(content)
            
            # Save to file
            filename = os.path.join(OUTPUT_DIR, f"{slug}.json")
            with open(filename, 'w', encoding='utf-8') as f:
                json.dump(json_data, f, indent=2)
            print(f"Saved {filename}")
            return True
        except json.JSONDecodeError:
            print(f"Failed to parse JSON for {slug}. URL might not be a valid roadmap JSON endpoint.")
            return False
    return False

def main():
    print(f"Fetching roadmaps list from {ROADMAPPED_URL}...")
    html = fetch_html(ROADMAPPED_URL)
    
    if not html:
        print("Failed to retrieve main roadmaps page.")
        return

    slugs = extract_roadmap_slugs(html)
    print(f"Found {len(slugs)} potential roadmap links.")
    
    success_count = 0
    for slug in slugs:
        # Be nice to the server
        time.sleep(0.5) 
        if fetch_and_save_json(slug):
            success_count += 1
            
    print(f"\nCompleted. Successfully saved {success_count} roadmaps to {OUTPUT_DIR}")

if __name__ == "__main__":
    main()
