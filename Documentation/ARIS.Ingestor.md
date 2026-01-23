# ARIS.Ingestor

**ARIS.Ingestor** is a background console application responsible for the "Crawl" and "Embed" phases of the ARIS pipeline. Its primary role is to fetch raw recruitment data from external sources (O*NET), standardize it, enrich it with vector embeddings, and store it in the Symmetric Vector Dictionary (PostgreSQL).

## 1. Architecture Overview

The Ingestor follows a strict ETL (Extract, Transform, Load) pipeline:

1.  **Extract (Crawl):** Connects to the O*NET Web Services API (v2) to fetch master lists of occupations, followed by detailed skills, tasks, and descriptions for each.
2.  **Transform (Embed):**
    *   Standardizes data into `RefRole` (Occupation) and `RefSkill` (Task/Skill) entities.
    *   Uses **Microsoft Semantic Kernel** connected to a local **Ollama** instance (`nomic-embed-text` model) to generate 768-dimensional vector embeddings for all text descriptions.
3.  **Load (Store):** Persists the structured, vectorized data into PostgreSQL with `pgvector` support, ensuring strict relational integrity between Roles and Skills.

## 2. Key Components

### `IngestionWorker.cs`
The main orchestrator (Hosted Service).
*   **Lifecycle:** Starts on application launch, runs the pipeline once, and gracefully shuts down.
*   **Workflow:**
    1.  Ensures Database & Tables exist (Auto-Migration).
    2.  Fetches the "Master List" of career clusters/occupations.
    3.  Iterates through occupations (currently capped for dev safety).
    4.  Checks for existence to prevent duplicate work (Idempotency).
    5.  Delegates details fetching to `OnetService`.
    6.  Delegates embedding generation to Semantic Kernel.
    7.  Saves `RefRole`, `RefSkill`, and `RefRoleSkill` links to DB.

### `Services/OnetService.cs`
The specialized HTTP client for O*NET.
*   **Base URL:** `https://api-v2.onetcenter.org/`
*   **Auth:** Uses `X-API-Key` header (Managed via User Secrets).
*   **Endpoints:**
    *   `/online/career_clusters/all`: Master list.
    *   `/online/occupations/{code}/`: Basic details (Title, Description).
    *   `/online/occupations/{code}/summary/tasks`: Granular task list.
    *   `/online/occupations/{code}/summary/skills`: granular skill list.
*   **Pagination:** Automatically handles `next` page links for large datasets.

## 3. Configuration & Setup

*   **Database:** Connects to `aris-postgres` Docker container.
*   **AI:** Connects to `Ollama` at `http://localhost:11434`.
*   **Secrets:** API Keys are stored in .NET User Secrets to prevent accidental commits.

## 4. Current Status (Jan 2026)
*   [x] O*NET API Integration (v2) - **997 Roles Ingested**
*   [x] Database Schema with Lowercase convention (`ref_roles`, `ref_skills`)
*   [x] Vector Embedding via Ollama (`nomic-embed-text`) - **100% Coverage**
*   [x] Roadmap.sh Integration - **22 Roadmaps Ingested (~2600 skills)**
*   [x] Relationship Linking (Roles <-> Skills) - **~29,000 Links**
