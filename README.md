# ARIS - Automated Recruitment Intelligence System

ARIS is a symmetric recruitment platform utilizing a **Hybrid Knowledge Graph + RAG** architecture to bridge the semantic gap between job seekers and recruiters. By mapping both resumes and job descriptions to a standardized "Clean Signal" schema, ARIS identifies implicit skills and provides explainable career guidance.

## 🚀 Key Features
- **Symmetric Vector Search:** Match candidates and jobs using a unified dictionary of 997 roles and 18,125 skills (O*NET v2 + Roadmap.sh).
- **RAG-Based Career Guidance:** Personalized job recommendations and skill gap analysis using Llama 3.1.
- **Graph-Grounding:** (In Progress) Explanations and validation of skills via Neo4j knowledge graph navigation.

## 🛠 Tech Stack
- **Backend:** ASP.NET Core (.NET 10)
- **Frontend:** React 19 (TypeScript, Vite)
- **Database:** PostgreSQL 17 + `pgvector`
- **Graph:** Neo4j 5.x
- **AI/LLM:** Microsoft.Extensions.AI, Ollama (Llama 3.1 & all-minilm)
- **Infrastructure:** Docker & Docker Compose

## 📂 Project Structure
```text
Development/
├── backend/src/
│   ├── ARIS.API/      # Main REST API & RAG Engine
│   ├── ARIS.Ingestor/ # Data ingestion (O*NET, Roadmap.sh)
│   └── ARIS.Shared/   # EF Core Context & Shared Entities
├── frontend/          # React 19 Dashboard
└── docker-compose.yml # Infrastructure (Postgres, Neo4j, Ollama)
```

## 🛠 Setup & Installation

### 1. Prerequisites
- Docker & Docker Compose
- .NET 10 SDK
- Node.js & npm
- [Ollama](https://ollama.com/) (Running locally)

### 2. Infrastructure
Spin up the databases:
```bash
docker-compose up -d
```

### 3. LLM Setup
Ensure the required models are pulled in Ollama:
```bash
ollama pull llama3.1
ollama pull all-minilm
```

### 4. Database Initialization
The `ARIS.Ingestor` service populates the reference dictionary. Note: Requires an O*NET API Key in your user secrets.
```bash
cd backend/src/ARIS.Ingestor
dotnet run
```

### 5. Start the API
```bash
cd backend/src/ARIS.API
dotnet run
```

### 6. Start the Frontend
```bash
cd frontend
npm install
npm run dev
```

## 📖 API Documentation
Detailed endpoint documentation can be found in `Documentation/ARIS.API.md`.

- **Scalar API Reference:** `http://localhost:5000/scalar/v1`
