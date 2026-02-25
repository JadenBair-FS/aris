# ARIS.Ingestor Documentation

**Last Updated:** 2026-02-25

**ARIS.Ingestor** is a console application responsible for the ETL (Extract, Transform, Load) pipeline that seeds both the PostgreSQL reference dictionary and the Neo4j knowledge graph. It runs once to populate the system and is safe to re-run — all ingestion steps are idempotent.

---

## 1. Architecture Overview

The Ingestor follows a strict ETL pipeline across two sequential phases:

**Phase 1 — Roadmap.sh (Technical Hierarchy)**
Ingests 22 community-maintained technology roadmaps from Roadmap.sh. All skills from this source are stored with `IsTech=true` and `source='Roadmap.sh'`. Prerequisite and peer relationships are read directly from the roadmap JSON edge data to produce real `SUBSET_OF` and `BRIDGE_TO` relationships rather than a flat star topology. This phase runs first to anchor the technical vocabulary and establish hierarchy before O*NET processing.

**Phase 2 — O*NET (Professional Taxonomy)**
Ingests all 997 occupations from O*NET Web Services v2. For each occupation, the ingestor fetches four endpoint types:
- Cognitive and psychomotor skills (O*NET 35-item skills taxonomy) → `IsTech=false`
- Domain knowledge areas (e.g., Building and Construction, Computer and Electronics) → `IsTech=false`
- Work activities (on-the-job tasks) → `IsTech=false`
- Technology skills (specific software tools and platforms) → `IsTech=true`

A cosine similarity deduplication step (threshold 0.15) prevents duplicate nodes when O*NET skills overlap with Roadmap.sh skills already in the dictionary.

**Phase 3 — Ontology Enrichment (BRIDGE_TO Edge Generation)**
After both sources are ingested, the Ontology Enrichment service uses Mistral 7B to identify lateral transferability relationships within each role's skill cluster. Results are persisted as bidirectional `BRIDGE_TO` edges in Neo4j.

---

## 2. Key Components

### `IngestionWorker.cs`
The main orchestrator hosted service.

- Runs the pipeline once on application launch and shuts down gracefully.
- Ensures the database schema is current via EF Core auto-migration before ingestion begins.
- Coordinates all three phases in order: Roadmap.sh → O*NET → Ontology Enrichment.
- Checks existence before inserting to prevent duplicate work (idempotency).

### `Services/OntologyEnrichmentService.cs`
The intelligence layer — solves the "static graph" problem by discovering horizontal transferability.

**Mechanism (Retrieval-Augmented Classification):**
1. Retrieves all skills required for a specific role from Neo4j.
2. Prompts Mistral 7B (via `OntologyEnrichment.md`) to identify which pairs within that skill set are highly transferable (e.g., MySQL ↔ PostgreSQL).
3. Constrains the LLM to select only from existing graph nodes — prevents hallucinated skill names from entering the graph.
4. Persists discovered pairs as bidirectional `BRIDGE_TO` edges in Neo4j.

Uses `ChatResponseFormat.Json` + `ChatMessage(ChatRole.System/User)` pattern (reference implementation for `IChatClient` usage in the project).

### `Services/Neo4jIngestionService.cs`
Driver for graph persistence.

- Enforces uniqueness constraints for `onet_code` (Role nodes) and `name` (Skill nodes).
- Manages creation of `REQUIRES`, `SUBSET_OF`, and `BRIDGE_TO` relationship types.
- Provides role-skill neighborhood clustering logic used by the enrichment phase.

---

## 3. Configuration & Setup

**Database:** Connects to the `aris-postgres` Docker container (PostgreSQL 17 with pgvector).

**AI / Embeddings:** Connects to Ollama at `http://192.168.4.172:11434`
- Embedding model: `qwen3-embedding:0.6b` — generates 1024-dimensional vectors for all skill and role nodes.
- Chat model: `mistral` — used by `OntologyEnrichmentService` for BRIDGE_TO edge classification.

**Secrets:** The O*NET API key is stored in .NET User Secrets (`dotnet user-secrets set "OnetApiKey" "..."`) to prevent accidental commits.

---

## 4. Ingestion Strategy

To ensure a high-integrity graph suitable for gap analysis, the ingestion pipeline implements **Ontological Reconciliation** across four principles:

1. **Roadmap First (Technical Hierarchy):** Roadmap.sh is processed before O*NET. This ensures programming and infrastructure skills (TypeScript, Docker, Kubernetes) are marked `source='Roadmap.sh'` before O*NET encounters them. If O*NET were processed first, these skills would be marked `source='ONET_Skill'` and the cross-domain filter that blocks programming skills from non-tech candidates would fail silently.

2. **Semantic Deduplication (Threshold 0.15):** Every incoming skill is compared via cosine similarity against all existing skills before insertion. If the closest match has distance < 0.15, the incoming skill is treated as a duplicate and the existing canonical entry is preserved. This collapses variants like "JavaScript", "JavaScript Programming Language", and "JS" into a single node.

3. **IsTech Flagging:** Skills from O*NET technology endpoints and all Roadmap.sh skills receive `IsTech=true`. General O*NET knowledge, skills, and work activity items receive `IsTech=false`. Domain tools (Salesforce, MEDITECH, Procore) carry `IsTech=true` from the O*NET technology endpoint but `source='ONET_Skill'`, so they are not blocked by the cross-domain filter.

4. **Relationship Hierarchy:**
   - `REQUIRES`: Role → Skill (from O*NET role-skill mappings)
   - `SUBSET_OF`: Child Skill → Parent Skill (directed upward, from Roadmap.sh prerequisites)
   - `BRIDGE_TO`: Skill ↔ Skill (bidirectional, from Ontology Enrichment LLM classification)

---

## 5. Post-Ingestion Reclassification

After initial ingestion, a targeted reclassification step was applied to correct 82 skills that were initially stored with `source='ONET_Skill'` despite being structurally connected to Roadmap.sh nodes (e.g., C#, React, TypeScript, Docker, Kubernetes). These had been deduplicated during ingestion — O*NET encountered them first in an earlier run — and were incorrectly tagged. The fix updated both the Neo4j node property and the PostgreSQL `ref_skills.source` column for all 82 skills to `'Roadmap.sh'` without requiring a full re-ingestion.

This correction eliminated domain leakage where programming skills appeared as bridgeable for non-technical candidates (Plumber, Sales).

---

## 6. Knowledge Graph Statistics (Final State)

| Node / Edge Type | Count | Source |
| :--- | :--- | :--- |
| Role nodes | 997 | O*NET (all career clusters) |
| Skill nodes | 12,981 | O*NET + Roadmap.sh (post-deduplication) |
| REQUIRES edges | 60,106 | O*NET role-skill mappings |
| SUBSET_OF edges | 10,150 | O*NET hierarchy + Roadmap.sh prerequisites |
| BRIDGE_TO edges | 16,812 | Ontology Enrichment (Mistral 7B classification) |

---

## 7. Current Status

- [x] O*NET API Integration (v2) — **997 roles ingested**
- [x] Roadmap.sh Integration — **22 roadmaps, ~2,600 skills**
- [x] Semantic deduplication — **0.15 cosine distance threshold**
- [x] Vector embeddings via Qwen3 (`qwen3-embedding:0.6b`) — **100% coverage, 1024d**
- [x] IsTech + Source dual-field flagging — **12,981 skills correctly classified**
- [x] 82-skill reclassification (ONET_Skill → Roadmap.sh) — **cross-domain filter restored**
- [x] Relationship linking — **60,106 REQUIRES, 10,150 SUBSET_OF, 16,812 BRIDGE_TO**
- [x] Ontology Enrichment — **BRIDGE_TO edges across all role clusters**
