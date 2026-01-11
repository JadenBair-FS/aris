import fitz 
import json
import ollama
import os

RESUME_SCHEMA = {
    "full_name": "string",
    "contact_info": {
        "email": "string",
        "linkedin": "string"
    },
    "skills": ["string"],
    "experience": [
        {
            "company": "string",
            "role": "string",
            "duration": "string",
            "highlights": ["string"]
        }
    ],
    "education": [
        {
            "institution": "string",
            "degree": "string",
            "year": "string"
        }
    ]
}

def extract_text_from_pdf(pdf_path):
    print(f"Reading PDF: {pdf_path}...")
    text = ""
    with fitz.open(pdf_path) as doc:
        for page in doc:
            text += page.get_text()
    return text

def structure_resume(raw_text):
    print("Structuring resume with Llama 3...")
    
    prompt = f"""
    You are an expert HR Data Scientist. Convert the following raw resume text into a valid JSON object.
    
    CRITICAL RULES:
    Output ONLY valid JSON.
    Follow this schema exactly: {json.dumps(RESUME_SCHEMA)}
    Normalize skill names (e.g., 'Javascript' -> 'JavaScript').
    If a field is missing, use null.
    
    RESUME TEXT:
    {raw_text}
    """
    
    response = ollama.chat(model="llama3", messages=[
        {"role": "user", "content": prompt}
    ])
    
    # Extract JSON from response
    content = response['message']['content']
    try:
        # Find the first { and last } to handle blocks
        start = content.find("{")
        end = content.rfind("}") + 1
        json_str = content[start:end]
        return json.loads(json_str)
    except Exception as e:
        print(f"Error parsing JSON from LLM: {e}")
        return {"error": "Failed to parse JSON", "raw": content}

def process_resume(pdf_path):
    raw_text = extract_text_from_pdf(pdf_path)
    if not raw_text.strip():
        return {"error": "No text found in PDF"}
        
    structured_data = structure_resume(raw_text)
    return structured_data

if __name__ == "__main__":
    pass
