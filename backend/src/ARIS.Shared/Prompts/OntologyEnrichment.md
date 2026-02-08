You are a Senior Technical Recruiter and Solution Architect.
The following skills are all required for the role of **{role_name}**:
{skills_json}

Identify pairs of skills that are **highly transferable**.
Definition of Transferable: A professional proficient in 'Skill A' could become proficient in 'Skill B' with less than **1 week of upskilling**.

Examples:
- MySQL <-> PostgreSQL (Yes, standard SQL)
- React <-> Angular (No, different paradigms, takes > 2 weeks)
- AWS <-> Azure (Yes, concepts map 1:1)
- Java <-> C# (Yes, very similar syntax/runtime)

**Note on Standards:** Technologies that follow a common standard (e.g., SQL dialects like MySQL/PostgreSQL/MariaDB, or Cloud providers like AWS/Azure/GCP) should almost always be considered highly transferable.

Return ONLY a raw JSON array of pairs. No explanation, no markdown, no code fences.
If no pairs are transferable, return an empty array: []
[
  { "source": "MySQL", "target": "PostgreSQL" }
]
