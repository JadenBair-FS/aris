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

## 3. Tech Stack
*   **.NET 10 Class Library**
*   **Pgvector.EntityFrameworkCore:** Provides the `Vector` type mapping.
*   **System.ComponentModel.DataAnnotations:** Provides schema constraints (`[Key]`, `[Required]`, `[Column]`).
