# ARIS.API Documentation

## Overview
`ARIS.API` is the core backend service for the ARIS platform. It orchestrates the Hybrid Graph-RAG architecture, managing vector similarity, graph traversal, and grounded text generation.

## Tech Stack
*   **Framework:** ASP.NET Core (.NET 10)
*   **Database:** PostgreSQL 17 (via `ARIS.Shared`) with `pgvector`
*   **Graph DB:** Neo4j 5.x (Community Edition)
*   **AI/ML:** 
    *   `Microsoft.Extensions.AI` for model-agnostic abstraction
    *   `Ollama` (local) running `all-minilm` for 384d embeddings
    *   `Ollama` (local) running `llama3.1` for RAG and reasoning
*   **PDF Processing:** `PdfPig`
*   **Document Generation:** `QuestPDF` (Upcoming)

## Core Services

### 1. ResumeService
Handles the "Clean Signal" ingestion pipeline for candidates.
*   **PDF Parsing:** Extracts raw text using `PdfPig`.
*   **Clean Signal Extraction:** Uses `llama3.1` to extract technical entities into JSON.
*   **Symmetric Grounding:** Map extracted terms to the nearest canonical entity in the Dictionary using vector similarity (Threshold < 0.6).
*   **Vectorization:** Generates professional identity embeddings for matching.

### 2. JobService
Handles job posting ingestion and recruiter-facing recommendations.
*   **Symmetric Ingestion:** Processes job descriptions using the exact same Clean Signal pipeline as resumes.
*   **Job Recommendation:** Performs semantic search (`pgvector`) to find candidates for a job or jobs for a candidate.
*   **Match Analysis:** Generates LLM-based summaries of the vector-level fit.

### 3. MatchService
The "Reasoning Engine" that executes the hybrid matching algorithm with **five-tier skill gap classification**:
1.  **Tier 1, Matched:** Explicitly listed in the user profile.
2.  **Tier 2, Implicitly Matched:** Not listed, but "owned" because the user knows a child specialization (e.g., React implies JavaScript).
3.  **Tier 3, Prerequisite Met:** Missing, but "Teachable" because the foundational technology is present via `SUBSET_OF` edges.
4.  **Tier 4, Bridgeable:** Reachable via lateral `BRIDGE_TO` edges.
5.  **Tier 5, Hard Gap:** Unreachable within the user's graph neighborhood.

### 4. GraphService
Executes Cypher queries against Neo4j to perform neighborhood traversal using the **Standardized Foundation Rule** $(Child)\text{-[:SUBSET\_OF]->}(Parent)$.
*   **Implicit Discovery:** Follow arrows outward to grant foundational skills based on specializations.
*   **Prerequisite Detection:** Follow arrows inward to validate if foundations for missing child skills are present.
*   **Valid Neighborhood ($V$):** Calculates the set of all skills reachable within 2 hops via structural edges ($REQUIRES$, $SUBSET\_OF$, $BRIDGE\_TO$).

### 5. GroundingService (The Circuit Breaker)
Enforces factual integrity by validating AI-generated claims against the Knowledge Graph.
*   **Grounding Score:** Calculates the ratio of verified skills to total generated entities.
*   **Hallucination Rejection:** Rejects or regenerates content if it contains claims outside the user's valid graph neighborhood.

### 6. TailoringService (Upcoming)
Orchestrates the resume optimization workflow.
*   **Grounded Rewrite:** Prompts the LLM to emphasize transferable skills identified by the MatchService.
*   **PDF Export:** Renders the final tailored JSON into an ATS-compliant PDF via `QuestPDF`.

## Key Endpoints

### 1. Match Analysis
*   **URL:** `GET /api/match/analyze/{userProfileId}/{jobId}`
*   **Response:** `MatchAnalysisResult` containing Similarity, Matching Skills, Prerequisite Met Skills, Bridgeable Skills, and Hard Gaps.

### 2. Job Recommendations
*   **URL:** `GET /api/job/recommend/{userProfileId}`
*   **Response:** Ranked list of matching jobs with "Smart Summaries."

### 3. Dictionary Search
*   **URL:** `POST /api/dictionary/search/[roles|skills]`
*   **Response:** Standardized entities from the PostgreSQL dictionary.