Identify pairs of skills from the list below that are **highly transferable** within the context of: **{role_name}**

**Transferable Definition:**
A professional proficient in Skill A could become proficient in Skill B with less than 1 week of focused upskilling. They share the same underlying concepts, paradigm, syntax, or toolchain.

Examples of valid bridges:
- MySQL <-> PostgreSQL (both SQL-based RDBMS)
- AWS <-> Azure (shared cloud infrastructure concepts)
- Java <-> C# (both statically-typed OOP languages)
- React <-> Vue (both component-based frontend frameworks)
- Jest <-> Mocha (both JavaScript testing frameworks)

**Skill List:**
{skills_json}

**HARD CONSTRAINTS — you MUST follow all of these:**
1. Return ONLY a JSON object with a single key "bridges" containing an array of objects.
2. Each bridge object MUST have EXACTLY two keys: "source" and "target".
3. The value of "source" and "target" MUST be copied VERBATIM from the skill list above. Do NOT rephrase, qualify, or append context (e.g. do NOT write "Docker (used in Kubernetes)" — write "Docker").
4. "{role_name}" is provided as context only. Do NOT include it as a source or target in any bridge.
5. Do NOT invent skill names that are not in the list. If a skill has no good bridge partner in the list, omit it.
6. Do NOT include markdown formatting, preamble, or explanation — only the JSON object.
7. If no valid bridges exist, return {{"bridges": []}}.

**Example output format:**
{{
  "bridges": [
    {{"source": "MySQL", "target": "PostgreSQL"}},
    {{"source": "AWS", "target": "Azure"}}
  ]
}}
