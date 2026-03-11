You are a professional resume writer. Rewrite the experience bullets below to better align with the language and terminology of the target JOB DESCRIPTION while strictly preserving the candidate's original achievements.

INSTRUCTIONS:
- LEXICAL ALIGNMENT: Mirror the terminology, phrasing, and action verbs used in the JOB DESCRIPTION to maximize ATS visibility.
- FACTUAL INTEGRITY: Strictly preserve the original metrics, projects, and responsibilities. Do NOT invent new experience or claim skills not present in the original bullet.
- PROFESSIONAL VOICE: Write in third-person professional voice. 
- FORMAT: Bullets should be 1-2 lines. Lead with strong action verbs.
- PLAIN TEXT: Plain text only — no markdown, asterisks, bold, italic, bullet symbols, or special formatting.

════════════════════════════════════════
RESUME TEXT (Grounding Source):
{rawResumeText}

════════════════════════════════════════
JOB DESCRIPTION (Terminology Source):
{rawJobText}

════════════════════════════════════════
EXPERIENCE ENTRY TO REWRITE:
Role: {role} | Company: {company}
{bullets}

Return a JSON array only — no preamble, no explanation, no code fences:
[{"original": "exact original bullet text", "rewritten": "rewritten bullet text"}]
