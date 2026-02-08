# ARIS.Shared

**ARIS.Shared** is the foundational Class Library that holds the "Truth" of the system. It defines the core data structures, entities, and contracts used by both the Ingestor (Writer) and the API (Reader). It ensures that all parts of the ARIS ecosystem speak the same language.

## 1. Core Mandates
*   **Database Agnostic (mostly):** While it uses EF Core attributes, the entities are designed to be clean POCOs (Plain Old CLR Objects).
*   **Vector First:** First-class support for `pgvector` types (`Vector`) to enable semantic search.
*   **Postgres Standard:** Enforces `snake_case` naming conventions for tables and columns to ensure seamless interoperability with standard SQL tools.

## 2. Entity Definitions (`Entities/`)

### `RefRole.cs` (Table: `ref_roles`)
Represents a standardized Job Role or Occupation (e.g., "Software Developer").
*   **`Id`**: Primary Key.
*   **`Title`**: The display name.
*   **`OnetCode`**: The unique O*NET SOC code (e.g., `15-1252.00`).
*   **`Description`**: The "Gold Standard" description used for embedding.
*   **`Embedding`**: A 384-dimensional vector representing the semantic meaning of the role.

### `RefSkill.cs` (Table: `ref_skills`)
Represents an atomic competency, task, or tool (e.g., "Python", "Debugging", "Critical Thinking").
*   **`Id`**: Primary Key.
*   **`Name`**: The unique name of the skill.
*   **`Description`**: Detailed context (if available).
*   **`Source`**: Origin tracking (e.g., `ONET_Task`, `ONET_Skill`, `Roadmap`).
*   **`Embedding`**: Vector representation for similarity matching.

### `RefRoleSkill.cs` (Table: `ref_role_skills`)
The Join Table representing the "Knowledge Graph" edges. It connects Roles to Skills.
*   **Composite Key:** (`RoleId`, `SkillId`).
*   **Metadata:**
    *   **`Importance`**: How critical is this skill to this role?
    *   **`Level`**: What proficiency is required?

### `UserProfile.cs` (Table: `user_profiles`)
Stores the "Symmetric Professional Identity" of a candidate.
*   **`CleanSignal`**: JSON representation of the candidate's skills and roles.
*   **`Embedding`**: 384d vector generated from the Clean Signal.
*   **`RawResume`**: JSON-wrapped raw text for auditability.

### `JobPosting.cs` (Table: `job_postings`)
Stores the "Symmetric Requirement Identity" of a job.
*   **`CleanSignal`**: JSON representation of the job's mandatory and preferred skills.
*   **`Embedding`**: 384d vector generated from the Clean Signal.

### `MatchAnalysisResult.cs`
The output DTO for the four-tier skill gap classification.
*   **`MatchingSkills`**: Skills present in both the user profile and the job requirements.
*   **`ImplicitlyDiscoveredSkills`**: Foundations granted because the user possesses child specializations (e.g., React implies JavaScript).
*   **`PrerequisiteMetSkills`**: Missing skills where the user possesses a direct parent technology.
*   **`BridgeableSkills`**: Missing skills reachable within the user's 2-hop graph neighborhood via lateral edges.
*   **`HardGaps`**: Missing skills with no reachable path in the Knowledge Graph.

## 3. Tech Stack
*   **.NET 10 Class Library**
*   **Pgvector.EntityFrameworkCore:** Provides the `Vector` type mapping.
*   **Microsoft.Extensions.AI.Abstractions:** Common AI models and contracts.
*   **System.ComponentModel.DataAnnotations:** Provides schema constraints (`[Key]`, `[Required]`, `[Column]`).
