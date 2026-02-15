Identify ALL pairs of skills that are **highly transferable** within the context of the role: **{role_name}**

**Transferable Definition:** 
A professional proficient in 'Skill A' could become proficient in 'Skill B' with less than 1 week of upskilling. They share the same underlying concepts, syntax, or logic.

Examples of Valid Bridges:
- MySQL <-> PostgreSQL (Both are SQL-based RDBMS)
- AWS <-> Azure (Both share cloud infrastructure concepts)
- Java <-> C# (Both are C-style OOP languages)
- React <-> Vue (Both are component-based frontend frameworks)

**Skill List:**
{skills_json}

**GOAL:**
Find as many valid bridges as possible from the list above. Do not limit yourself to just one.

**CONSTRAINTS:**
1. Return ONLY a JSON object with a single key "bridges" containing an array of objects.
2. Each bridge object MUST have EXACTLY two keys: "source" and "target".
3. Do NOT include any markdown formatting (like ```json), preamble, or explanation.
4. If no bridges are found, return {"bridges": []}.
5. Ensure the skill names match exactly as provided in the list.

Example Output:
{
  "bridges": [
    {"source": "MySQL", "target": "PostgreSQL"},
    {"source": "AWS", "target": "Azure"}
  ]
}
