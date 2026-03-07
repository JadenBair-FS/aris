Extract the job description below into a JSON object with exactly these 4 keys: "target_roles", "required_skills", "responsibilities", "minimum_education".

Rules:
1. Skill names must be 1-5 words (e.g. "Python", "Sterile Technique", "Budget Administration"). Never use a full sentence as a skill name — strip any requirement prose down to the core competency.
2. Academic degree requirements (bachelor's, master's, Ph.D.) belong in "minimum_education", not "required_skills". Professional certifications (BLS, PMP, CST) stay in "required_skills".
3. Set "importance" based on the section the skill appears in:
   - "Essential" — Required, Qualifications, Minimum Requirements, responsibilities/duties, or any unlabeled list.
   - "Preferred" — Preferred Qualifications, Nice to Have, Desired, Bonus.
   - Default to "Essential" if the posting does not distinguish.
4. For "years_of_experience": if the line containing the skill has a years qualifier (e.g. "3+ years of X"), assign that number to the skill. Multiple skills on one line share the same years value. If no years qualifier appears on the skill's line, use 0.0. Never apply a general role-level experience statement (e.g. "5+ years in the field") to individual skills. Always output a number, never null.
5. If a skill close-matches the Reference Vocabulary, use the reference name.
6. If "required_skills" would be fewer than 3 items from the qualifications section alone, also extract competencies from the responsibilities section.

### SOFT SKILLS VOCABULARY:
When extracting soft skills, prefer exact names from this list. Only include a soft skill if clearly stated or strongly implied.
{soft_skills}

### REFERENCE VOCABULARY:
Roles: {reference_roles}
Skills: {reference_skills}

### SCHEMA:
{
  "target_roles": [ { "title": "string", "priority": "Primary/Secondary" } ],
  "required_skills": [ { "name": "string", "importance": "Essential/Preferred", "years_of_experience": number } ],
  "responsibilities": [ "string" ],
  "minimum_education": [ { "degree": "string", "required": boolean } ]
}

### JOB DESCRIPTION:
{raw_text}

Return ONLY the JSON object. No preamble, no markdown.
