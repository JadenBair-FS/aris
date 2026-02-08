# ARIS.Ingestor

**ARIS.Ingestor** is a background console application responsible for the "Crawl" and "Embed" phases of the ARIS pipeline. Its primary role is to fetch raw recruitment data from external sources (O*NET), standardize it, enrich it with vector embeddings, and store it in the Symmetric Vector Dictionary (PostgreSQL).

## 1. Architecture Overview

The Ingestor follows a strict ETL (Extract, Transform, Load) pipeline:

1.  **Extract (Crawl):** Connects to the O*NET Web Services API (v2) to fetch master lists of occupations, followed by detailed skills, tasks, and descriptions for each.
2.  **Transform (Embed):**
    *   Standardizes data into `RefRole` (Occupation) and `RefSkill` (Task/Skill) entities.
    *   Uses **Microsoft Semantic Kernel** connected to a local **Ollama** instance (`all-minilm` model) to generate 384-dimensional vector embeddings for all text descriptions.
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

### `Services/OntologyEnrichmentService.cs`
The "Intelligence" layer of the ingestion pipeline.
*   **Role:** Solves the "Static Graph" problem by discovering horizontal transferability relationships.
*   **Mechanism (Retrieval-Augmented Classification):**
    1.  Retrieves all skills required for a specific Role from Neo4j.
    2.  Prompts `llama3.1` to identify which pairs within that set are highly transferable (e.g., MySQL <-> PostgreSQL).
    3.  Constrains the LLM to only select from existing graph nodes, preventing hallucination.
    4.  Persists discovered pairs as bidirectional `BRIDGE_TO` edges.

### `Services/Neo4jIngestionService.cs`
The driver for graph persistence.
*   **Constraints:** Enforces uniqueness for `onet_code` (Roles) and `name` (Skills).
*   **Relationships:** Manages `REQUIRES`, `SUBSET_OF`, and `BRIDGE_TO` edge creation.
*   **Analytics:** Provides clustering logic for role-skill neighborhoods.

## 3. Configuration & Setup

*   **Database:** Connects to `aris-postgres` Docker container.
*   **AI:** Connects to `Ollama` at `http://localhost:11434`.
*   **Secrets:** API Keys are stored in .NET User Secrets to prevent accidental commits.

## 4. Ingestion Strategy (Feb 2026 Update)

To ensure a high-integrity graph suitable for Gap Analysis, the ingestion pipeline implements **Ontological Reconciliation**.

1.  **Roadmap First (Vocabulary & technical hierarchy):** Technical roadmaps are ingested first to establish the industry-standard technical hierarchy. Specialized child nodes point to foundations: $(Child)\text{-[:SUBSET\_OF]->}(Parent)$.
2.  **Semantic Deduplication:** During every ingestion step, the system performs a **Vector-Similarity check** (Threshold < 0.15). Incoming skills are mapped to existing canonical anchors if they are semantically identical (e.g., "Using Docker" maps to "Docker").
3.  **O*NET Second (Professional Context):** O*NET Occupations are ingested and snapped to the clean technical vocabulary established in Step 1.
4.  **Relationship Hierarchy:** The system establishes three relationship types:
    *   `REQUIRES`: Role -> Skill.
    *   `SUBSET_OF`: Child -> Parent (Vertical foundation).
    *   `BRIDGE_TO`: Peer <-> Peer (Horizontal transferability).

## 5. Current Status
*   [x] O*NET API Integration (v2) - **997 Roles Ingested**
*   [x] Database Schema with Lowercase convention (`ref_roles`, `ref_skills`)
*   [x] Vector Embedding via Ollama (`all-minilm`) - **100% Coverage**
*   [x] Roadmap.sh Integration - **22 Roadmaps Ingested (~2600 skills)**
*   [x] **Symmetric Grounding** - implemented to normalize synonyms.
*   [x] **Role Anchoring** - implemented to connect O*NET Roles to Roadmap Roots.
*   [x] Relationship Linking (Roles <-> Skills) - **~29,000 Links**
