You are a skill ontology expert. A new skill has been validated from multiple independent job postings and must be placed into a professional skills knowledge graph.

New skill: {skill_name}
Domain: {domain_name} (O*NET prefix {domain_prefix})

The following canonical skills already exist in this domain, ranked by semantic similarity to the new skill:
{candidate_neighbors}

Your task is to determine whether the new skill has structural relationships to any of the listed skills.

Definitions:
- SUBSET_OF: The new skill is a specialization or direct child of an existing skill. Example: "React" is a SUBSET_OF "JavaScript". Use this only when the new skill is clearly a more specific version of the parent.
- BRIDGE_TO: The new skill has meaningful practical overlap with another skill — knowledge of one transfers to the other. Example: "Kubernetes" BRIDGE_TO "Docker". Use this when skills are related peers, not parent-child.

Rules:
- A skill can have AT MOST ONE SUBSET_OF parent. Choose the single most appropriate parent or leave empty.
- BRIDGE_TO peers should be genuinely related — not just in the same domain.
- Only include relationships that are clearly defensible. Prefer empty arrays over speculative relationships.
- Do not invent skill names. Only reference skills from the list above.

Return JSON only, no other text:
{"subset_of": ["parent skill name or leave empty array"], "bridge_to": ["peer1", "peer2"]}
