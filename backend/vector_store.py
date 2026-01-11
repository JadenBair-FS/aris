import os
import json
import logging
import psycopg2
from psycopg2.extras import Json
from pgvector.psycopg2 import register_vector
import ollama
from dotenv import load_dotenv

load_dotenv()

# DB Connection Config
DB_NAME = os.getenv("POSTGRES_DB", "aris_db")
DB_USER = os.getenv("POSTGRES_USER", "postgres")
DB_PASS = os.getenv("POSTGRES_PASSWORD", "password123")
DB_HOST = os.getenv("POSTGRES_HOST", "localhost")
DB_PORT = os.getenv("POSTGRES_PORT", "5432")

# Setup Logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

class VectorStore:
    def __init__(self):
        self.conn = psycopg2.connect(
            dbname=DB_NAME,
            user=DB_USER,
            password=DB_PASS,
            host=DB_HOST,
            port=DB_PORT
        )
        self.conn.autocommit = True
        self._initialize_db()

    def close(self):
        self.conn.close()

    def _initialize_db(self):
        """Enable pgvector and create the necessary tables."""
        with self.conn.cursor() as cur:
            # Enable extension
            cur.execute("CREATE EXTENSION IF NOT EXISTS vector;")
            register_vector(self.conn)
            
            # Create User Profiles table
            cur.execute("""
                CREATE TABLE IF NOT EXISTS user_profiles (
                    user_id TEXT PRIMARY KEY,
                    data JSONB,
                    embedding VECTOR(768),
                    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );
            """)

            # Create Job Postings table (Symmetric to user_profiles)
            cur.execute("""
                CREATE TABLE IF NOT EXISTS job_postings (
                    job_id TEXT PRIMARY KEY,
                    recruiter_id TEXT,
                    data JSONB,
                    embedding VECTOR(768),
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );
            """)

            # Create Recruiters table (Standard SQL, no vector needed)
            cur.execute("""
                CREATE TABLE IF NOT EXISTS recruiters (
                    recruiter_id TEXT PRIMARY KEY,
                    data JSONB,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );
            """)
            logger.info("PostgreSQL Vector Store initialized (Symmetric: Users & Jobs + Recruiters).")

    def _get_embedding(self, text):
        """Generate embedding using Ollama (nomic-embed-text)."""
        try:
            response = ollama.embeddings(model="nomic-embed-text", prompt=text)
            return response["embedding"]
        except Exception as e:
            logger.error(f"Error generating embedding: {e}")
            return []

    # Recruiter Profile Methods
    
    def save_recruiter(self, recruiter_data, recruiter_id):
        """Saves recruiter/company profile."""
        query = """
        INSERT INTO recruiters (recruiter_id, data, created_at)
        VALUES (%s, %s, CURRENT_TIMESTAMP)
        ON CONFLICT (recruiter_id) DO UPDATE 
        SET data = EXCLUDED.data;
        """
        with self.conn.cursor() as cur:
            cur.execute(query, (recruiter_id, Json(recruiter_data)))
        return recruiter_id

    def get_recruiter(self, recruiter_id):
        query = "SELECT data FROM recruiters WHERE recruiter_id = %s;"
        with self.conn.cursor() as cur:
            cur.execute(query, (recruiter_id,))
            record = cur.fetchone()
            return record[0] if record else None

    # Candidate Profile Methods

    def save_profile(self, profile_json, user_id="default_user"):
        """Saves structured profile with 'Clean Signal' Vector embedding."""
        skills_text = ", ".join(profile_json.get("skills", []))
        roles = ", ".join([exp.get('role', '') for exp in profile_json.get("experience", [])])
        
        # CLEAN SIGNAL: Only embed skills and roles to avoid raw text noise
        full_text = f"Professional Skills: {skills_text}. Roles: {roles}."
        
        embedding = self._get_embedding(full_text)
        if not embedding: return None

        query = """
        INSERT INTO user_profiles (user_id, data, embedding, updated_at)
        VALUES (%s, %s, %s, CURRENT_TIMESTAMP)
        ON CONFLICT (user_id) DO UPDATE 
        SET data = EXCLUDED.data, embedding = EXCLUDED.embedding, updated_at = CURRENT_TIMESTAMP;
        """
        with self.conn.cursor() as cur:
            cur.execute(query, (user_id, Json(profile_json), embedding))
        return user_id

    def get_profile(self, user_id):
        query = "SELECT data FROM user_profiles WHERE user_id = %s;"
        with self.conn.cursor() as cur:
            cur.execute(query, (user_id,))
            record = cur.fetchone()
            return record[0] if record else None

    def get_job(self, job_id):
        """Retrieves a stored job posting."""
        query = "SELECT data FROM job_postings WHERE job_id = %s;"
        with self.conn.cursor() as cur:
            cur.execute(query, (job_id,))
            record = cur.fetchone()
            return record[0] if record else None

    # Job Posting Methods

    def save_job(self, job_json, job_id, recruiter_id="default_recruiter"):
        """Saves structured job description with 'Clean Signal' Vector embedding."""
        title = job_json.get("title", "Unknown Role")
        requirements = ", ".join(job_json.get("requirements", []))
        
        # CLEAN SIGNAL: Only embed title and requirements to match candidate symmetry
        full_text = f"Job Requirement. Role: {title}. Skills: {requirements}."
        
        embedding = self._get_embedding(full_text)
        if not embedding: return None

        query = """
        INSERT INTO job_postings (job_id, recruiter_id, data, embedding, created_at)
        VALUES (%s, %s, %s, %s, CURRENT_TIMESTAMP)
        ON CONFLICT (job_id) DO UPDATE 
        SET data = EXCLUDED.data, embedding = EXCLUDED.embedding;
        """
        with self.conn.cursor() as cur:
            cur.execute(query, (job_id, recruiter_id, Json(job_json), embedding))
        return job_id

    # Symmetric Search Methods

    def search_jobs(self, user_id, limit=5):
        """Finds jobs that match a user's profile embedding."""
        with self.conn.cursor() as cur:
            cur.execute("SELECT embedding FROM user_profiles WHERE user_id = %s;", (user_id,))
            user_record = cur.fetchone()
            if not user_record: return []
            user_embedding = user_record[0]

            cur.execute("""
                SELECT job_id, data, 1 - (embedding <=> %s) as similarity
                FROM job_postings
                ORDER BY similarity DESC LIMIT %s;
            """, (user_embedding, limit))
            return cur.fetchall()

    def search_candidates(self, job_id, limit=5):
        """Finds candidates that match a job's embedding."""
        with self.conn.cursor() as cur:
            cur.execute("SELECT embedding FROM job_postings WHERE job_id = %s;", (job_id,))
            job_record = cur.fetchone()
            if not job_record: return []
            job_embedding = job_record[0]

            cur.execute("""
                SELECT user_id, data, 1 - (embedding <=> %s) as similarity
                FROM user_profiles
                ORDER BY similarity DESC LIMIT %s;
            """, (job_embedding, limit))
            return cur.fetchall()

if __name__ == "__main__":
    store = VectorStore()
    print("Vector Store Symmetric Setup Complete.")
    store.close()