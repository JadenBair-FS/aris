You are a Senior Software Architect and Technical Curriculum Designer.
The following skills are all required for the role of **{role_name}**:
{skills_json}

Identify pairs of skills where one is a **technical prerequisite** (parent) of the other (child).
Definition: Skill B is a SUBSET_OF Skill A if learning Skill A is a **hard prerequisite** for using Skill B. Skill B cannot be used without first knowing Skill A.

Examples:
- React -> JavaScript (Yes, React is built on JavaScript)
- ASP.NET Core -> C# (Yes, ASP.NET Core requires C#)
- Django -> Python (Yes, Django requires Python)
- Docker -> Linux (Yes, Docker requires Linux fundamentals)
- TypeScript -> JavaScript (Yes, TypeScript is a superset of JavaScript)
- React -> CSS (No, CSS is useful but not a hard prerequisite)
- PostgreSQL -> SQL (Yes, PostgreSQL requires SQL knowledge)
- AWS -> Linux (No, AWS can be used without Linux)

Only include relationships where the dependency is **fundamental and unavoidable**, not merely helpful.

Return ONLY a raw JSON array. No explanation, no markdown, no code fences.
If no dependencies exist, return an empty array: []
[
  { "child": "React", "parent": "JavaScript" }
]
