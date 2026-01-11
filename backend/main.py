import os
import shutil
import uuid
import json
import jwt
from jwt.algorithms import RSAAlgorithm
import requests
from fastapi import FastAPI, UploadFile, File, HTTPException, Depends, Header
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
from parse_resume import process_resume
from vector_store import VectorStore
from enhancer import ResumeEnhancer

app = FastAPI(title="ARIS API - Automated Resume Intelligence System")

# Enable CORS for frontend
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"], # In production, restrict this
    allow_methods=["*"],
    allow_headers=["*"],
)

# Global State
store = VectorStore()
enhancer = ResumeEnhancer()
JWKS_CACHE = None

# Authentication Logic
# Ideally, cache these keys to avoid fetching on every request
JWKS_URL = "https://large-llama-50.clerk.accounts.dev/.well-known/jwks.json"

def get_current_user_id(authorization: str = Header(None)):
    """
    Validates the Clerk JWT token and returns the user_id (sub).
    """
    global JWKS_CACHE
    if not authorization:
        raise HTTPException(status_code=401, detail="Missing Authorization Header")
    
    token = authorization.replace("Bearer ", "")
    
    try:
        # Fetch Clerk's Public Keys 
        if JWKS_CACHE is None:
            jwks = requests.get(JWKS_URL).json()
            JWKS_CACHE = jwks
        else:
            jwks = JWKS_CACHE
        
        # Get the Key ID from the token header
        unverified_header = jwt.get_unverified_header(token)
        rsa_key = {}
        
        for key in jwks["keys"]:
            if key["kid"] == unverified_header["kid"]:
                rsa_key = RSAAlgorithm.from_jwk(json.dumps(key))
                break
                
        if not rsa_key:
            raise HTTPException(status_code=401, detail="Invalid Token Key")

        # Decode and Verify
        payload = jwt.decode(
            token,
            rsa_key,
            algorithms=["RS256"],
            options={"verify_aud": False} # Clerk tokens often don't have audience set for frontend
        )
        
        return payload["sub"] # The User ID
        
    except jwt.ExpiredSignatureError:
        raise HTTPException(status_code=401, detail="Token Expired")
    except Exception as e:
        print(f"Auth Error: {e}")
        raise HTTPException(status_code=401, detail="Invalid Authentication")

# Endpoints

class JDRequest(BaseModel):
    jd_text: str

class JobDescription(BaseModel):
    job_id: str
    title: str
    requirements: list[str]
    description: str

class RecruiterProfile(BaseModel):
    company_name: str
    website: str
    industry: str
    logo_url: str = None

@app.on_event("startup")
async def startup_event():
    await enhancer.initialize()

@app.on_event("shutdown")
def shutdown_event():
    store.close()
    enhancer.close()

@app.get("/")
def read_root():
    return {"message": "Welcome to ARIS API"}

# Recruiter Endpoints

@app.post("/recruiter-profile")
async def create_recruiter_profile(
    profile: RecruiterProfile,
    user_id: str = Depends(get_current_user_id)
):
    store.save_recruiter(profile.dict(), recruiter_id=user_id)
    return {"status": "success", "recruiter_id": user_id}

@app.get("/recruiter-profile")
async def get_recruiter_profile(
    user_id: str = Depends(get_current_user_id)
):
    profile = store.get_recruiter(recruiter_id=user_id)
    if not profile:
        raise HTTPException(status_code=404, detail="Recruiter profile not found.")
    return profile

@app.post("/upload-resume")
async def upload_resume(
    file: UploadFile = File(...), 
    user_id: str = Depends(get_current_user_id)
):
    if not file.filename.endswith(".pdf"):
        raise HTTPException(status_code=400, detail="Only PDF files are supported.")
    
    # Save temp file
    temp_id = str(uuid.uuid4())
    temp_path = f"/tmp/{temp_id}.pdf"
    
    try:
        with open(temp_path, "wb") as buffer:
            shutil.copyfileobj(file.file, buffer)
        
        # Parse Resume
        structured_data = process_resume(temp_path)
        if "error" in structured_data:
            raise HTTPException(status_code=500, detail=structured_data["error"])
        
        # Store in PostgreSQL (Scoped to User)
        store.save_profile(structured_data, user_id=user_id)
        
        return structured_data
    finally:
        if os.path.exists(temp_path):
            os.remove(temp_path)

@app.get("/profile")
async def get_profile(user_id: str = Depends(get_current_user_id)):
    profile = store.get_profile(user_id=user_id)
    if not profile:
        raise HTTPException(status_code=404, detail="No profile found. Please upload a resume first.")
    return profile

@app.post("/analyze")
async def analyze_job(
    request: JDRequest,
    user_id: str = Depends(get_current_user_id)
):
    profile = store.get_profile(user_id=user_id)
    if not profile:
        raise HTTPException(status_code=404, detail="No profile found. Please upload a resume first.")
    
    # Analyze Gaps & Strengths
    analysis = await enhancer.analyze_gaps_and_strengths(profile, request.jd_text)
    
    # Enhance Strengths
    suggestions = await enhancer.enhance_strengths(profile, analysis['strengths'])
    
    return {
        "analysis": analysis,
        "suggestions": suggestions
    }

@app.post("/post-job")
async def post_job(
    job: JobDescription,
    user_id: str = Depends(get_current_user_id)
):
    # Store the job in the vector store
    # user_id here acts as the recruiter_id
    success = store.save_job(job.dict(), job.job_id, recruiter_id=user_id)
    if not success:
        raise HTTPException(status_code=500, detail="Failed to save job posting.")
    return {"status": "success", "job_id": job.job_id}

@app.get("/match-jobs")
async def match_jobs(
    user_id: str = Depends(get_current_user_id)
):
    matches = store.search_jobs(user_id=user_id)
    return {"matches": matches}

@app.get("/match-candidates/{job_id}")
async def match_candidates(
    job_id: str,
    user_id: str = Depends(get_current_user_id)
):
    # Ensure the recruiter owns this job? (Optional for now)
    matches = store.search_candidates(job_id=job_id)
    return {"matches": matches}

class CandidateAnalysisRequest(BaseModel):
    user_id: str
    job_id: str

@app.post("/analyze-candidate")
async def analyze_candidate(
    request: CandidateAnalysisRequest,
    user_id: str = Depends(get_current_user_id) # The Recruiter
):
    # Get the candidate profile
    profile = store.get_profile(user_id=request.user_id)
    if not profile:
        raise HTTPException(status_code=404, detail="Candidate profile not found.")
    
    # Get the job description
    # We need a get_job method in VectorStore
    job_record = store.get_job(request.job_id)
    if not job_record:
        raise HTTPException(status_code=404, detail="Job posting not found.")
    
    # Analyze
    jd_text = f"{job_record['title']} {job_record['description']} {', '.join(job_record['requirements'])}"
    analysis = await enhancer.analyze_gaps_and_strengths(profile, jd_text)
    
    # Generate Recruiter-focused summary
    summary = await enhancer.generate_recruiter_summary(profile, jd_text, analysis)
    
    return {
        "candidate_id": request.user_id,
        "analysis": analysis,
        "recruiter_summary": summary
    }

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)