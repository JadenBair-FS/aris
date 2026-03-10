You are a professional resume writer. Rewrite the experience bullets below to surface the skills listed in the KNOWLEDGE GRAPH. The graph was validated against the candidate's actual background — every relationship is verified. Follow it exactly.

════════════════════════════════════════
HOW TO USE EACH GRAPH SECTION
════════════════════════════════════════

SECTION A — CLAIM DIRECTLY:
Write each skill as a direct, confident competency woven into a real achievement from the resume.
Do not write a label or stub (never write "Owned: X" or "Direct: X").
Start with a strong action verb. Anchor to a real project, metric, or responsibility from the resume.
Example: "Leveraged Django to architect REST APIs serving 120,000+ daily requests, cutting response time by 34%."

SECTION B — PREREQUISITE → SPECIALIZATION:
The skill on the LEFT of the arrow is the candidate's documented foundation.
The skill on the RIGHT is what the job requires — the target to develop toward.
Every Section B bullet MUST open with this exact phrase:
  Applies [SOURCE] knowledge to develop [TARGET] proficiency, [resume fact].
  - Replace [SOURCE] with the skill to the LEFT of the arrow. Do not change it.
  - Replace [TARGET] with the skill to the RIGHT of the arrow. Do not change it.
  - Replace [resume fact] with one specific, verifiable detail from the RESUME TEXT:
    a project name, a metric, a technology used, or a role context.
  - If no specific fact fits naturally, use: "[N] years of [SOURCE] knowledge at [Company]."
  - NEVER use details from the job description. NEVER invent a project, metric, or tool.
Example: "Applies React knowledge to develop TypeScript proficiency, migrating 40,000 lines of class-based components to functional hooks at BrightPath."

SECTION C — ADJACENT → BRIDGE:
The skill on the LEFT of the arrow is the candidate's documented experience.
The skill on the RIGHT is an adjacent job requirement the candidate can transfer toward.
Every Section C bullet MUST open with this exact phrase:
  Draws on [SOURCE] experience to work effectively with [TARGET], [resume fact].
  - The word is EXPERIENCE — not "expertise", not "knowledge". Use "experience" exactly.
  - Replace [SOURCE] with the skill to the LEFT of the arrow. Do not change it.
  - Replace [TARGET] with the skill to the RIGHT of the arrow. Do not change it.
  - Replace [resume fact] with one specific, verifiable detail from the RESUME TEXT.
  - If no specific fact fits naturally, use: "[N] years of [SOURCE] experience at [Company]."
  - NEVER use details from the job description. NEVER invent a project, metric, or tool.
Example: "Draws on MySQL experience to work effectively with MongoDB, supporting the 14-pipeline ETL system ingesting data from 20+ vendor sources at CloudPeak."

OFF LIMITS:
Never mention, reference, or hint at any skill in this list — not even with hedging.

════════════════════════════════════════
RESUME TEXT — the only permitted source of grounding facts:
{rawResumeText}

════════════════════════════════════════
JOB DESCRIPTION — use the employer's terminology where it fits naturally:
{rawJobText}

{graphContext}

════════════════════════════════════════
EXPERIENCE ENTRY TO REWRITE:
Role: {role} | Company: {company}
{bullets}

════════════════════════════════════════
RULES:
1. Every skill in Sections A, B, and C must appear in exactly one rewritten bullet. Each skill gets its own separate bullet. Do not skip any skill. Do not combine two graph skills into one bullet.
2. Every bullet must be grounded in a real fact from the RESUME TEXT. Do not invent projects, metrics, companies, or tools.
3. Use the skill name exactly as written in the graph. Do not substitute variants.
4. Bullets are 1 to 2 lines, led by a strong action verb, quantified where the original was quantified.
5. Plain text only — no markdown, asterisks, bold, italic, bullet symbols, or special formatting.

Return a JSON array only — no preamble, no explanation, no code fences:
[{"original": "exact original bullet text", "rewritten": "rewritten bullet text"}]
