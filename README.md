# ARIS - Automated Recruitment Intelligence System

ARIS is a symmetric recruitment platform utilizing a **Hybrid Knowledge Graph + RAG** architecture to bridge the semantic gap between job seekers and recruiters. By mapping both resumes and job descriptions to a standardized "Clean Signal" schema, ARIS identifies implicit skills and provides explainable career guidance.

## Key Features

- **Symmetric Vector Search:** Match candidates and jobs using a unified dictionary of 997 roles and 12,981 skills (O*NET v2 + Roadmap.sh), embedded with Qwen3 (1024-dimensional vectors).
- **Five-Tier Skill Gap Classification:** Matched, Implicitly Matched, Prerequisite Met, Bridgeable, and Hard Gap — scored as the ArisScore blended ranking signal.
- **Hybrid Graph-RAG:** Neo4j knowledge graph traversal (REQUIRES, SUBSET_OF, BRIDGE_TO edges) combined with pgvector cosine similarity for explainable skill bridging.
- **Clean Signal Pipeline:** Mistral 7B extracts structured JSON from raw resume PDFs and job descriptions, grounded against the O*NET + Roadmap.sh reference dictionary.
- **Resume Tailoring:** Rewrites experience bullets to highlight transferable skills toward specific job gaps.

## Tech Stack

| Component | Technology |
| :--- | :--- |
| Backend | ASP.NET Core (.NET 10) |
| Frontend | React 19 (TypeScript, Vite) |
| Vector DB | PostgreSQL 17 + `pgvector` |
| Graph DB | Neo4j 5.x Community |
| LLM (chat/RAG) | Mistral 7B via Ollama |
| Embeddings | Qwen3 (`qwen3-embedding:0.6b`, 1024d) via Ollama |
| AI Abstraction | `Microsoft.Extensions.AI` |
| PDF Parsing | PdfPig |
| Infrastructure | Docker & Docker Compose |

## Project Structure

```text
Development/
├── backend/src/
│   ├── ARIS.API/      # Main REST API, RAG engine, and evaluation endpoints
│   ├── ARIS.Ingestor/ # ETL pipeline (O*NET v2, Roadmap.sh, Ontology Enrichment)
│   └── ARIS.Shared/   # EF Core context, entities, DTOs, and LLM prompt templates
├── frontend/          # React 19 dashboard (Vite scaffold)
├── scripts/           # Benchmark and bulk-upload utilities
└── docker-compose.yml # Infrastructure (PostgreSQL, Neo4j)
```

## Setup & Installation

### 1. Prerequisites

- Docker & Docker Compose
- .NET 10 SDK
- Node.js & npm
- [Ollama](https://ollama.com/) running on a GPU host

### 2. Infrastructure

```bash
cd Development
docker-compose up -d
```

### 3. LLM Setup

Pull the required models in Ollama:

```bash
ollama pull mistral
ollama pull qwen3-embedding:0.6b
```

### 4. Database Initialization

The `ARIS.Ingestor` populates the reference dictionary (O*NET + Roadmap.sh). Requires an O*NET API key stored in .NET User Secrets:

```bash
cd Development/backend/src/ARIS.Ingestor
dotnet user-secrets set "OnetApiKey" "<your-key>"
dotnet run
```

### 5. Start the API

```bash
cd Development/backend/src/ARIS.API
dotnet run
```

### 6. Start the Frontend

```bash
cd Development/frontend
npm install
npm run dev
```

## API Documentation

Interactive docs: `https://localhost:7293/scalar/v1` (development only)

Detailed service documentation: `Documentation/ARIS.API.md`, `Documentation/ARIS.Shared.md`, `Documentation/ARIS.Ingestor.md`
