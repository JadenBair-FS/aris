# ARIS.API Documentation

**Last Updated:** 2026-02-25

## Overview
`ARIS.API` is the core backend service for the ARIS platform. It orchestrates the Hybrid Graph-RAG architecture, managing vector similarity search, knowledge graph traversal, grounded text generation, and the Clean Signal extraction pipeline.

---

## Tech Stack

| Component | Technology | Notes |
| :--- | :--- | :--- |
| Framework | ASP.NET Core (.NET 10) | |
| Vector DB | PostgreSQL 17 + `pgvector` | Via `ARIS.Shared` / EF Core |
| Graph DB | Neo4j 5.x Community | Cypher queries via `Neo4j.Driver` |
| LLM (chat/RAG) | Mistral 7B via Ollama | `http://192.168.4.172:11434` |
| Embeddings | Qwen3 (`qwen3-embedding:0.6b`) via Ollama | 1024-dimensional vectors |
| AI Abstraction | `Microsoft.Extensions.AI` | Model-agnostic `IChatClient` / `IEmbeddingGenerator` |
| PDF Parsing | `PdfPig` | Resume PDF text extraction |
| API Docs | Scalar (`/scalar/v1`) | OpenAPI 3.x interactive docs |

---

## Services

### 1. `ResumeService`
Handles the full Clean Signal ingestion pipeline for job seekers.

1. **PDF Parsing** — Extracts raw text from uploaded PDFs via `PdfPig`.
2. **Reference Vocabulary Retrieval** — Embeds the raw text and queries pgvector for the 8 most similar roles and 30 most similar skills to inject into the LLM prompt as grounding context.
3. **Clean Signal Extraction** — Calls Mistral 7B with the `ResumeExtraction.md` prompt to produce a structured `ResumeCleanSignal` JSON object (roles, skills, experience summary, education).
4. **Post-processing** — Normalizes field names, strips prose sentences from skill names, runs `LenientStringConverter` + `LenientDoubleConverter` to handle LLM JSON deviations.
5. **Retry Logic** — Up to 3 extraction attempts with a correction prompt that includes the previous malformed output.
6. **Symmetric Grounding** — Maps extracted skill names to the nearest canonical entry in the dictionary using cosine similarity (threshold < 0.35).
7. **Embedding** — Generates a 1024-dimensional Qwen3 embedding from the Clean Signal symmetric string.
8. **Persistence** — Saves `UserProfile` to PostgreSQL with the raw resume, Clean Signal JSON, and embedding.

Also exposes `TailorResumeAsync` — runs `AnalyzeMatchAsync` on a user-job pair and uses Mistral to rewrite resume bullet points to highlight transferable skills for bridgeable and prerequisite-met gaps.

### 2. `JobService`
Handles job posting ingestion and recommendation.

1. **Clean Signal Extraction** — Same LLM pipeline as resumes, using `JobExtraction.md`. Extracts `target_roles`, `required_skills` (with `importance` and `years_of_experience`), `responsibilities`, and `minimum_education`.
2. **Sparse Skill Fallback** — If fewer than 3 skills are extracted (common for narrative/academic postings), a second LLM pass over the responsibilities list extracts implied competencies.
3. **Domain-Biased Grounding** — Grounds extracted skill names against skills linked to the primary O*NET role's SOC family before falling back to a global search. Non-tech postings exclude `Roadmap.sh` skills.
4. **Embedding + Persistence** — Stores `JobPosting` with the raw description, Clean Signal JSON, and 1024-dimensional embedding.
5. **Recommendation** — `GetRecommendedJobsAsync` queries pgvector for jobs similar to a user profile (cosine distance < 0.65), then generates an LLM narrative summary of the top 3 matches.

### 3. `MatchService`
The reasoning engine. Executes the hybrid five-tier skill gap classification and produces the ArisScore.

**`AnalyzeMatchAsync(userProfileId, jobId)`** pipeline:
1. Load both `UserProfile.CleanSignal` and `JobPosting.CleanSignal`.
2. Compute vector similarity via pgvector cosine distance.
3. Classify each required job skill into one of five tiers:
   - **Tier 1, Matched** — Skill explicitly in the user's profile.
   - **Tier 2, Implicitly Matched** — Granted via upward `SUBSET_OF` traversal (e.g., React → JavaScript).
   - **Tier 3, Prerequisite Met** — User has the parent technology (e.g., has JavaScript, job needs React).
   - **Tier 4, Bridgeable** — Reachable via lateral `BRIDGE_TO` edges within 2 hops.
   - **Tier 5, Hard Gap** — Unreachable; certifications are always hard gaps regardless of graph path.
4. Compute `GraphCoverageScore` using importance weights (Essential=1.0, Preferred=0.6) and an experience multiplier for Tier 1 skills: `μ = max(0.5, candidateYears/requiredYears)`.
5. Compute `ArisScore = 0.55 × VectorSimilarity + 0.45 × GraphCoverageScore`.

**`GenerateGroundedSummaryAsync(userProfileId, jobId)`** — Runs `AnalyzeMatchAsync`, serializes the tier results as context, and prompts Mistral via `MatchAnalysis.md` to produce a natural-language match summary. Returns the summary text and the Graph Grounding Score (ratio of verifiable skill mentions).

### 4. `GraphService`
Executes Cypher queries against Neo4j for neighborhood traversal.

- **`GetImplicitlyDiscoveredSkillsAsync`** — Follows `SUBSET_OF` edges **upward** (child → parent) from the candidate's skill nodes to discover implicitly owned foundational skills.
- **`GetPrerequisiteMetSkillsAsync`** — Checks whether the candidate owns any direct parent (`SUBSET_OF` downward) of the missing skill.
- **`GetBridgeableSkillsAsync`** — Traverses `BRIDGE_TO` lateral edges up to 2 hops from the candidate's skill neighborhood, filtered by the IsTech + Source dual filter for non-tech candidates.
- **`GetValidNeighborhoodAsync`** — Returns the full set of skills reachable within 2 hops for Graph Grounding Score calculation.

Relationship types used: `REQUIRES`, `SUBSET_OF`, `BRIDGE_TO`. Non-existent types (`RELATED_TO`, `IS_PARENT_OF`) are not queried.

### 5. `GroundingService`
Validates AI-generated text against the knowledge graph.

- **`CalculateGroundingScoreAsync(text, userSkills)`** — Extracts skill names mentioned in `text` via substring matching against canonical skill names, then computes `Score = |generated ∩ valid_neighborhood| / |generated|`. A score ≥ 0.95 indicates graph-grounded output. Returns a `GroundingResult` with the score and matched/unmatched skill lists.
- **`ExtractSkillsFromText`** — Substring-matching implementation that identifies canonical skill names present in arbitrary text.

### 6. `DictionaryService`
Vector search and RAG recommendations over the reference dictionary.

- `SearchRolesAsync` / `SearchSkillsAsync` — Embeds the query and returns top-k similar roles or skills from `ref_roles` / `ref_skills`.
- `GetJobRecommendationsAsync` / `GetSkillRecommendationsAsync` — Performs vector retrieval then passes results to Mistral for a narrative RAG response.

### 7. `ExtractionBenchmarkService`
Benchmark service for the thesis latency evaluation (Section J of the methodology).

- Accepts an `ExtractionBenchmarkRequest` specifying text, type (`"job"` | `"resume"`), number of runs, and list of model names.
- Creates a fresh `OllamaApiClient` per model, retrieves reference vocabulary once (not timed), then times only the `GetResponseAsync` LLM inference call for each run.
- Returns per-model `LatencyMs[]`, `AvgLatencyMs`, parsed CleanSignal output, skill count, role count, and a `BenchmarkComparison` (speedup factor, shared skills, skills unique to the larger model).
- Does not write to the database or perform grounding.

---

## API Endpoints

### Resume (`/api/resume`)

| Method | Path | Description |
| :--- | :--- | :--- |
| `POST` | `/api/resume/upload` | Upload a PDF resume (`multipart/form-data`). Returns `{ id: Guid }`. |
| `POST` | `/api/resume/upload-text` | Upload resume as plain text (`{ content, userId }`). Returns `{ id: Guid }`. |
| `GET` | `/api/resume/{id:guid}` | Fetch a user profile's full CleanSignal and metadata. |
| `POST` | `/api/resume/tailor` | Tailor resume bullets toward a job (`{ userProfileId, jobId }`). Returns `List<TailoredBullet>`. |

### Job (`/api/job`)

| Method | Path | Description |
| :--- | :--- | :--- |
| `POST` | `/api/job` | Create a job posting (`{ description, recruiterId }`). Returns `{ jobId: Guid }`. |
| `GET` | `/api/job/match/{userProfileId:guid}` | Get recommended jobs for a user profile. Returns ranked list with LLM summary. |
| `GET` | `/api/job/{id:guid}` | Fetch a job posting's full CleanSignal and metadata. |

### Match (`/api/match`)

| Method | Path | Description |
| :--- | :--- | :--- |
| `POST` | `/api/match/analyze` | Run full five-tier gap analysis (`{ userProfileId, jobId }`). Returns `MatchAnalysisResult`. |
| `POST` | `/api/match/summary` | Generate grounded LLM match summary (`{ userProfileId, jobId }`). Returns `{ summary, groundingScore }`. |
| `GET` | `/api/match/debug/jobs` | List all job postings (debug). |
| `GET` | `/api/match/debug/users` | List all user profiles (debug). |
| `GET` | `/api/match/debug/user/{id}` | Fetch user profile detail (debug). |
| `GET` | `/api/match/debug/job/{id}` | Fetch job posting detail (debug). |

### Recruiter (`/api/recruiter`)

| Method | Path | Description |
| :--- | :--- | :--- |
| `GET` | `/api/recruiter/job/{jobId:guid}/candidates` | Bidirectional search — finds top-N candidate profiles for a job by cosine similarity, then runs `AnalyzeMatchAsync` on each. Returns ranked candidates with gap analysis. |

### Dictionary (`/api/dictionary`)

| Method | Path | Description |
| :--- | :--- | :--- |
| `POST` | `/api/dictionary/search/roles` | Vector search over `ref_roles` (`{ query }`). |
| `POST` | `/api/dictionary/search/skills` | Vector search over `ref_skills` (`{ query }`). |
| `POST` | `/api/dictionary/recommend/jobs` | RAG recommendation for job roles (`{ query }`). |
| `POST` | `/api/dictionary/recommend/skills` | RAG recommendation for skills (`{ query }`). |

### Eval (`/api/eval`)

| Method | Path | Description |
| :--- | :--- | :--- |
| `POST` | `/api/eval/grounding` | Compute Graph Grounding Score for arbitrary text against a user's valid skill neighborhood (`{ text, userProfileId }`). Thesis RQ2 metric. |
| `POST` | `/api/eval/implicit-skills` | Get per-tier skill counts and Implicit Discovery Rate for a user-job pair (`{ userProfileId, jobId }`). Thesis RQ1 metric. |
| `POST` | `/api/eval/pipeline-compare` | Run all three evaluation pipelines (A: LLM Direct, B: Vector-RAG, C: ARIS Graph-RAG) on a user-job pair for thesis RQ1/RQ2/RQ3 comparison. |
| `POST` | `/api/eval/extraction-benchmark` | Benchmark LLM vs SLM extraction latency and quality (`{ text, type, runs, models }`). Thesis Section J latency metric. |

---

## Configuration

**`appsettings.json` / `appsettings.Development.json`:**
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5433;Database=aris;Username=...;Password=..."
  }
}
```

**Ollama (GPU server):** `http://192.168.4.172:11434`
- Chat model: `mistral` (Mistral 7B, Q4_K_M)
- Embedding model: `qwen3-embedding:0.6b` (1024d)
- Both clients initialized with a 1-hour timeout.

**Neo4j:** `bolt://localhost:7687` (configured in `GraphService`)

**Scalar API Docs:** `https://localhost:7293/scalar/v1` (development only)
