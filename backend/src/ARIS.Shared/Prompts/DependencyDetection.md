Identify ALL pairs of skills where one is a **technical prerequisite** (parent) of the other (child) for the role: **{role_name}**

**Dependency Definition:** 
Skill B is a SUBSET_OF Skill A if learning Skill A is a hard prerequisite for using Skill B. Skill B cannot be used without knowing Skill A. Skill A is more fundamental.

Examples:
- React -> JavaScript (React is built on JS)
- ASP.NET Core -> C# (ASP.NET Core uses C#)
- PostgreSQL -> SQL (PostgreSQL is an implementation of SQL)
- Docker -> Linux (Docker uses Linux primitives)

**Skill List:**
{skills_json}

**GOAL:**
Identify ALL such relationships in the provided list. Do not limit yourself to just one.

**CONSTRAINTS:**
1. Return ONLY a JSON object with a single key "dependencies" containing an array of objects.
2. Each dependency object MUST have EXACTLY two keys: "child" and "parent".
3. Do NOT include any markdown formatting (like ```json), preamble, or explanation.
4. If no dependencies are found, return {"dependencies": []}.
5. Ensure the skill names match exactly as provided in the list.

Example Output:
{
  "dependencies": [
    {"child": "React", "parent": "JavaScript"},
    {"child": "ASP.NET Core", "parent": "C#"}
  ]
}
