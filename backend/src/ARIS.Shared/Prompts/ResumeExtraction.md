Extract the resume below into a JSON object with exactly these 4 keys: "roles", "skills", "experience_summary", "education".

Rules:
- Extract only what is explicitly stated. Do not infer tools or languages that are not mentioned.
- Skills must be discrete names (e.g. "Python", "Agile"), not sentences.
- For each skill, set "years_of_experience" to the total years used across all roles (number, 0.0 if unknown — never null).
- "is_current" must be a JSON boolean.
- For soft skills, prefer exact names from the Soft Skills list below. Only include a soft skill if it is clearly stated or strongly implied.
- If a skill in the text is a close match to the Reference Vocabulary, use the reference name.

### SOFT SKILLS VOCABULARY:
{soft_skills}

### REFERENCE VOCABULARY:
Roles: {reference_roles}
Skills: {reference_skills}

### SCHEMA:
{
  "roles": [ { "title": "string", "duration": "string", "is_current": boolean } ],
  "skills": [ { "name": "string", "category": "Technical/Soft", "proficiency": "Expert/Advanced/Intermediate/Beginner", "years_of_experience": number } ],
  "experience_summary": [ { "role": "string", "company": "string", "bullets": ["string"] } ],
  "education": [ { "degree": "string", "institution": "string", "year": "string" } ]
}

### RESUME TEXT:
{raw_text}

Return ONLY the JSON object. No preamble, no markdown.
