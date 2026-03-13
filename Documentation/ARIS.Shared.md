# ARIS.Shared Documentation

**Last Updated:** 2026-02-25

**ARIS.Shared** is the foundational class library that holds the source of truth for the entire system. It defines all core data structures, entities, DTOs, and contracts used by both `ARIS.API` (reader) and `ARIS.Ingestor` (writer). Both projects reference this library — nothing is duplicated between them.

---

## 1. Core Mandates

- **Database Agnostic (mostly):** Entities use EF Core attributes but are clean POCOs — no HTTP or business logic.
- **Vector First:** First-class support for `pgvector` types (`Vector`) on all entities that participate in semantic search.
- **Postgres Standard:** Enforces `snake_case` naming for all tables and columns via `[Column]` attributes.
- **Shared Prompts:** LLM prompt templates (`*.md`) live in `Prompts/` and are copied to the build output directory at compile time, enabling runtime loading without recompilation.

---

## 2. Entity Definitions (`Entities/`)

### `RefRole.cs` — Table: `ref_roles`
Represents a standardized job role or occupation (e.g., "Software Developer").

| Property | Column | Type | Notes |
| :--- | :--- | :--- | :--- |
| `Id` | `id` | `int` | Primary key |
| `Title` | `title` | `string` | Display name |
| `OnetCode` | `onet_code` | `string?` | O*NET SOC code (e.g., `15-1252.00`) |
| `Description` | `description` | `string?` | Used as the embedding source text |
| `Embedding` | `embedding` | `vector(1024)` | 1024-dimensional Qwen3 embedding |
| `RoleSkills` | — | `List<RefRoleSkill>` | Navigation property |

### `RefSkill.cs` — Table: `ref_skills`
Represents an atomic competency, task, tool, or technology (e.g., "Python", "Debugging", "Salesforce").

| Property | Column | Type | Notes |
| :--- | :--- | :--- | :--- |
| `Id` | `id` | `int` | Primary key |
| `Name` | `name` | `string` | Unique canonical skill name |
| `Description` | `description` | `string?` | Detailed context if available |
| `Source` | `source` | `string?` | `"ONET_Skill"` or `"Roadmap.sh"` |
| `IsTech` | `is_tech` | `bool` | `true` for programming/infrastructure skills |
| `Embedding` | `embedding` | `vector(1024)` | 1024-dimensional Qwen3 embedding |

> **Source + IsTech cross-domain filter:** A skill is blocked from bridging to non-tech candidates only when both `IsTech=true` AND `source='Roadmap.sh'`. Domain tools (Salesforce, MEDITECH, Procore) carry `IsTech=true` from O*NET but `source='ONET_Skill'` and are not blocked.

### `RefRoleSkill.cs` — Table: `ref_role_skills`
Join table connecting roles to their required skills (O*NET role-skill mappings).

| Property | Column | Type | Notes |
| :--- | :--- | :--- | :--- |
| `RoleId` | `role_id` | `int` | Composite PK, FK → `ref_roles` |
| `SkillId` | `skill_id` | `int` | Composite PK, FK → `ref_skills` |
| `Importance` | `importance` | `double?` | O*NET importance score |
| `Level` | `level` | `double?` | O*NET proficiency level |

### `UserProfile.cs` — Table: `user_profiles`
Stores the processed professional identity of a job seeker.

| Property | Column | Type | Notes |
| :--- | :--- | :--- | :--- |
| `Id` | `id` | `Guid` | Primary key |
| `UserId` | `user_id` | `string` | External auth ID (e.g., Clerk) |
| `RawResume` | `raw_resume` | `jsonb` | Original parsed text for auditability |
| `CleanSignal` | `clean_signal_json` | `jsonb` | Deserialized `ResumeCleanSignal` |
| `Embedding` | `embedding` | `vector(1024)` | Generated from the Clean Signal symmetric string |
| `CreatedAt` | `created_at` | `DateTime` | UTC |
| `UpdatedAt` | `updated_at` | `DateTime` | UTC |

### `JobPosting.cs` — Table: `job_postings`
Stores the processed requirement identity of a job.

| Property | Column | Type | Notes |
| :--- | :--- | :--- | :--- |
| `Id` | `id` | `Guid` | Primary key |
| `RecruiterId` | `recruiter_id` | `string` | External auth ID |
| `RawDescription` | `raw_description` | `text` | Original job posting text |
| `CleanSignal` | `clean_signal_json` | `jsonb` | Deserialized `JobPostingCleanSignal` |
| `Embedding` | `embedding` | `vector(1024)` | Generated from the Clean Signal symmetric string |
| `CreatedAt` | `created_at` | `DateTime` | UTC |
| `UpdatedAt` | `updated_at` | `DateTime` | UTC |

---

## 3. Clean Signal Models (`Models/CleanSignal/`)

The Clean Signal schema is the normalized JSON representation of both resumes and job postings. Both sides are mapped to the same shared vocabulary space before any matching occurs.

### `ResumeCleanSignal.cs`
```
{
  "roles":              [ { "title", "duration", "is_current" } ]
  "skills":             [ { "name", "category", "proficiency", "years_of_experience" } ]
  "experience_summary": [ { "role", "company", "bullets": ["..."] } ]
  "education":          [ { "degree", "institution", "year" } ]
}
```

### `JobPostingCleanSignal.cs`
```
{
  "target_roles":    [ { "title", "priority" } ]              // "Primary" | "Secondary"
  "required_skills": [ { "name", "importance", "years_of_experience" } ]  // "Essential" | "Preferred"
  "responsibilities": ["..."]
  "minimum_education": [ { "degree", "required" } ]
}
```

---

## 4. Match & Gap Analysis Models (`Models/`)

### `MatchAnalysisResult.cs`
The primary output DTO for the five-tier skill gap classification engine.

| Property | Type | Description |
| :--- | :--- | :--- |
| `JobId` | `Guid` | The job posting evaluated against |
| `VectorSimilarity` | `double` | Raw cosine similarity from pgvector |
| `ArisScore` | `double` | Blended ranking signal: `0.55 × VectorSimilarity + 0.45 × GraphCoverageScore` |
| `MatchingSkills` | `List<SkillGapItem>` | **Tier 1:** Explicitly present in both profile and job |
| `ImplicitlyDiscoveredSkills` | `List<string>` | **Tier 2:** Granted via upward `SUBSET_OF` traversal |
| `PrerequisiteMetSkills` | `List<SkillGapItem>` | **Tier 3:** Missing, but candidate has the parent technology |
| `BridgeableSkills` | `List<SkillGapItem>` | **Tier 4:** Reachable via `BRIDGE_TO` lateral edges |
| `HardGaps` | `List<SkillGapItem>` | **Tier 5:** Unreachable in the candidate's graph neighborhood |

### `SkillGapItem.cs`
Enriched per-skill DTO returned in all non-string tier lists.

| Property | Type | Description |
| :--- | :--- | :--- |
| `SkillName` | `string` | Canonical skill name |
| `Importance` | `string` | `"Essential"` or `"Preferred"` from job posting |
| `YearsRequired` | `double` | Years of experience required by the job |
| `CandidateYears` | `double` | Candidate's stated years (Tier 1 only; 0.0 for Tiers 2–4) |
| `BridgePath` | `string?` | Human-readable path (e.g., `"via MySQL (BRIDGE_TO)"`) |
| `BridgeSource` | `string?` | `"Roadmap.sh"`, `"OntologyEnrichment"`, or `null` |

### `BenchmarkModels.cs`
DTOs for the extraction latency benchmark endpoint (`POST /api/eval/extraction-benchmark`).

- `ExtractionBenchmarkRequest` — `Text`, `Type` (`"job"` | `"resume"`), `Runs`, `Models`
- `ModelBenchmarkResult` — `LatencyMs[]`, `AvgLatencyMs`, `Output` (parsed CleanSignal), `SkillCount`, `RoleCount`, `Success`, `Error`
- `ExtractionBenchmarkResponse` — `Type`, `Runs`, `Results` (per-model), `Comparison`
- `BenchmarkComparison` — `FasterModel`, `SpeedupFactor`, `SkillCounts`, `SharedSkills`, `UniqueToLargerModel`

---

## 5. Prompts (`Prompts/`)

LLM prompt templates stored as `.md` files, copied to the build output directory at compile time and loaded at runtime. Edit these files to change LLM behavior without recompiling.

| File | Used By | Purpose |
| :--- | :--- | :--- |
| `ResumeExtraction.md` | `ResumeService` | Parses raw resume text into `ResumeCleanSignal` JSON |
| `JobExtraction.md` | `JobService` | Parses raw job description into `JobPostingCleanSignal` JSON |
| `MatchAnalysis.md` | `JobService` | Generates LLM summary of top job matches |
| `OntologyEnrichment.md` | `ARIS.Ingestor` | Identifies `BRIDGE_TO` skill pairs during graph construction |

---

## 6. Tech Stack

- **.NET 10 Class Library**
- **Pgvector.EntityFrameworkCore** — `vector(1024)` type mapping for PostgreSQL
- **Microsoft.EntityFrameworkCore** — EF Core ORM and migrations
- **System.ComponentModel.DataAnnotations** — Schema constraints (`[Key]`, `[Required]`, `[Column]`)
- **Microsoft.Extensions.AI.Abstractions** — Shared AI model contracts
