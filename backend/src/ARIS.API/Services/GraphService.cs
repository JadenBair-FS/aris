using ARIS.Shared.Models;
using ARIS.Shared.Models.CleanSignal;
using Neo4j.Driver;

namespace ARIS.API.Services;

public class GraphService : IDisposable, IAsyncDisposable
{
    private readonly IDriver _driver;
    private readonly ILogger<GraphService> _logger;

    public GraphService(IConfiguration configuration, ILogger<GraphService> logger)
    {
        _logger = logger;
        var uri = configuration["Neo4j:Uri"] ?? "bolt://localhost:7687";
        var user = configuration["Neo4j:User"] ?? "neo4j";
        var password = configuration["Neo4j:Password"] ?? "aris_password_local";

        _logger.LogInformation("Connecting to Neo4j at {Uri} as user {User}", uri, user);
        _driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
    }

    /// <summary>
    /// Returns the valid skill neighborhood: the user's known skills plus all reachable neighbors
    /// within 2 hops via SUBSET_OF (up and down) and IS_SIMILAR_TO edges.
    /// When includeTechSkills is false, only Roadmap.sh-sourced tech skills are excluded —
    /// domain tool bridges (CRM, EHR, etc.) that carry is_tech=true but source='ONET_Skill' are still allowed.
    /// </summary>
    public async Task<HashSet<string>> GetValidNeighborhoodAsync(IEnumerable<string> userSkills, bool includeTechSkills = true)
    {
        var validSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expansionSeed = userSkills.ToList();

        foreach (var skill in userSkills)
        {
            validSkills.Add(skill);
        }

        const string query = @"
            MATCH (s:Skill)
            WHERE toLower(s.name) IN [term IN $expansionSeed | toLower(term)]
            CALL {
                WITH s
                MATCH (s)-[:SUBSET_OF*1..2]->(parent:Skill)
                WHERE ($includeTech OR NOT (coalesce(parent.is_tech, false) AND parent.source = 'Roadmap.sh'))
                  AND parent.source <> 'ONET_Taxonomy'
                RETURN parent.name as Name
                UNION
                MATCH (child:Skill)-[:SUBSET_OF*1..2]->(s)
                WHERE ($includeTech OR NOT (coalesce(child.is_tech, false) AND child.source = 'Roadmap.sh'))
                  AND child.source <> 'ONET_Taxonomy'
                RETURN child.name as Name
                UNION
                MATCH (s)-[:IS_SIMILAR_TO]-(neighbor:Skill)
                WHERE ($includeTech OR NOT (coalesce(neighbor.is_tech, false) AND neighbor.source = 'Roadmap.sh'))
                  AND neighbor.source <> 'ONET_Taxonomy'
                RETURN neighbor.name as Name
            }
            RETURN DISTINCT Name
        ";

        try
        {
            await using var session = _driver.AsyncSession();
            var result = await session.ExecuteReadAsync(async tx => {
                var cursor = await tx.RunAsync(query, new { expansionSeed, includeTech = includeTechSkills });
                return await cursor.ToListAsync();
            });

            foreach (var record in result)
            {
                var name = record["Name"].As<string>();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    validSkills.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve graph neighborhood.");
        }

        return validSkills;
    }

    public async Task<HashSet<string>> GetPrerequisiteMetSkillsAsync(IEnumerable<string> userSkills, IEnumerable<string> missingSkills, bool includeTechSkills = true)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expansionSeed = userSkills.ToList();

        const string query = @"
            MATCH (parent:Skill)
            WHERE toLower(parent.name) IN [s IN $expansionSeed | toLower(s)]
            MATCH (child:Skill)-[:SUBSET_OF*1..2]->(parent)
            WHERE toLower(child.name) IN [s IN $missingSkills | toLower(s)]
              AND ($includeTech OR NOT (coalesce(child.is_tech, false) AND child.source = 'Roadmap.sh'))
              AND child.source <> 'ONET_Taxonomy'
            RETURN DISTINCT child.name AS Name

            UNION

            MATCH (foundation:Skill)
            WHERE toLower(foundation.name) IN [s IN $expansionSeed | toLower(s)]
            MATCH (foundation)-[:SUBSET_OF*1..2]->(target:Skill)
            WHERE toLower(target.name) IN [s IN $missingSkills | toLower(s)]
              AND ($includeTech OR NOT (coalesce(target.is_tech, false) AND target.source = 'Roadmap.sh'))
              AND target.source <> 'ONET_Taxonomy'
            RETURN DISTINCT target.name AS Name
        ";

        try
        {
            await using var session = _driver.AsyncSession();
            var records = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(query, new { expansionSeed, missingSkills = missingSkills.ToList(), includeTech = includeTechSkills });
                return await cursor.ToListAsync();
            });

            foreach (var record in records)
            {
                var name = record["Name"].As<string>();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve prerequisite-met skills.");
        }

        return result;
    }

    /// <summary>
    /// Returns skills implicitly granted because the user knows a child specialization
    /// (UP traversal only: child → parent via SUBSET_OF, max 2 hops).
    /// Thesis Tier 2: "the candidate knows a specialization, so the foundation is implicitly known."
    /// IS_SIMILAR_TO neighbors belong to Tier 4 (Bridgeable) and are excluded here.
    /// DOWN traversal (parent → children) belongs in GetPrerequisiteMetSkillsAsync.
    /// </summary>
    public async Task<Dictionary<string, string>> GetImplicitlyDiscoveredSkillsAsync(IEnumerable<string> userSkills, bool includeTechSkills = true)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var expansionSeed = userSkills.ToList();

        const string query = @"
            MATCH (child:Skill)
            WHERE toLower(child.name) IN [s IN $expansionSeed | toLower(s)]
            MATCH (child)-[:SUBSET_OF*1..2]->(parent:Skill)
            WHERE ($includeTech OR NOT (coalesce(parent.is_tech, false) AND parent.source = 'Roadmap.sh'))
              AND parent.source <> 'ONET_Taxonomy'
            RETURN parent.name AS Name, min(child.name) AS ViaSkill
        ";

        try
        {
            await using var session = _driver.AsyncSession();
            var records = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(query, new { expansionSeed, includeTech = includeTechSkills });
                return await cursor.ToListAsync();
            });

            foreach (var record in records)
            {
                var name = record["Name"].As<string>();
                var viaSkill = record["ViaSkill"].As<string>();
                if (!string.IsNullOrWhiteSpace(name) && !result.ContainsKey(name))
                    result[name] = viaSkill ?? "";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve implicit parent skills.");
        }

        return result;
    }

    /// <summary>
    /// Returns bridge path data for missing skills reachable from user skills via IS_SIMILAR_TO or SUBSET_OF edges.
    /// Used by MatchService to enrich SkillGapItem objects with BridgePath and BridgeSource context.
    /// </summary>
    public async Task<List<(string SkillName, string ViaSkill, string BridgeType, string? BridgeSource)>> GetBridgeablePathsAsync(
        IEnumerable<string> userSkills, IEnumerable<string> missingSkills)
    {
        var result = new List<(string, string, string, string?)>();
        var userList = userSkills.ToList();
        var missingList = missingSkills.ToList();

        if (userList.Count == 0 || missingList.Count == 0)
            return result;

        const string bridgeQuery = @"
            MATCH (u:Skill)-[r:IS_SIMILAR_TO]-(missing:Skill)
            WHERE toLower(u.name) IN [s IN $userSkills | toLower(s)]
              AND toLower(missing.name) IN [s IN $missingSkills | toLower(s)]
            RETURN missing.name AS SkillName, u.name AS ViaSkill, 'IS_SIMILAR_TO' AS BridgeType, r.source AS BridgeSource
        ";

        const string subsetQuery = @"
            MATCH (u:Skill)-[:SUBSET_OF*1..2]-(missing:Skill)
            WHERE toLower(u.name) IN [s IN $userSkills | toLower(s)]
              AND toLower(missing.name) IN [s IN $missingSkills | toLower(s)]
              AND NOT (u.source = 'ONET_Taxonomy' AND missing.source = 'ONET_Taxonomy')
            RETURN missing.name AS SkillName, u.name AS ViaSkill, 'SUBSET_OF' AS BridgeType, null AS BridgeSource
        ";

        try
        {
            await using var session = _driver.AsyncSession();

            var bridgeRecords = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(bridgeQuery, new { userSkills = userList, missingSkills = missingList });
                return await cursor.ToListAsync();
            });

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in bridgeRecords)
            {
                var skillName = record["SkillName"].As<string>();
                var viaSkill = record["ViaSkill"].As<string>();
                var bridgeType = record["BridgeType"].As<string>();
                var bridgeSource = record["BridgeSource"].As<string?>();

                if (!string.IsNullOrWhiteSpace(skillName) && seen.Add(skillName))
                    result.Add((skillName, viaSkill, bridgeType, bridgeSource));
            }

            var subsetRecords = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(subsetQuery, new { userSkills = userList, missingSkills = missingList });
                return await cursor.ToListAsync();
            });

            foreach (var record in subsetRecords)
            {
                var skillName = record["SkillName"].As<string>();
                var viaSkill = record["ViaSkill"].As<string>();
                var bridgeType = record["BridgeType"].As<string>();

                if (!string.IsNullOrWhiteSpace(skillName) && seen.Add(skillName))
                    result.Add((skillName, viaSkill, bridgeType, null));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve bridgeable skill paths.");
        }

        return result;
    }

    /// <summary>
    /// Serializes the most relevant graph paths between user skills and job skills
    /// into a structured string for injection as LLM grounding context.
    /// </summary>
    public async Task<string> GetGraphContextForMatchAsync(IEnumerable<string> userSkills, IEnumerable<string> jobSkills)
    {
        var paths = await GetBridgeablePathsAsync(userSkills, jobSkills);
        if (paths.Count == 0)
            return "No graph bridge paths found between candidate skills and job requirements.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Graph-grounded skill bridges:");
        foreach (var (skillName, viaSkill, bridgeType, bridgeSource) in paths.Take(15))
        {
            var sourceNote = bridgeSource != null ? $" [source: {bridgeSource}]" : "";
            sb.AppendLine($"  - {viaSkill} → {skillName} (via {bridgeType}{sourceNote})");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds a human-readable graph context block from a precomputed MatchAnalysisResult.
    /// Pure in-memory — uses data already computed by AnalyzeMatchAsync. No Neo4j queries.
    /// Used to inject validated skill relationships into the resume tailoring LLM prompt.
    /// </summary>
    /// <param name="match">Precomputed match analysis result.</param>
    /// <param name="candidateSkills">
    /// Optional candidate skill list (from ResumeCleanSignal.Skills).
    /// When provided, FROM-skill names in bridge hints are resolved to their original
    /// (pre-grounding) names so the LLM can match them back to the resume text.
    /// </param>
    public string BuildTailoringGraphContext(
        MatchAnalysisResult match,
        IEnumerable<ResumeSkill>? candidateSkills = null,
        IReadOnlySet<string>? alreadyUsedSkills = null)
    {
        var skills = candidateSkills?.ToList() ?? [];

        // canonical name → display name (original pre-grounding name, or canonical if unchanged)
        var canonicalToDisplay = skills
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var s = g.First();
                return s.OriginalName ?? s.Name;
            }, StringComparer.OrdinalIgnoreCase);

        // canonical name → documented years on resume
        var canonicalToYears = skills
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().YearsOfExperience, StringComparer.OrdinalIgnoreCase);

        // set of skills documented directly on the resume (for detecting fabricated bridge sources)
        var documentedSkills = new HashSet<string>(
            skills.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

        bool IsUsed(string skillName) =>
            alreadyUsedSkills != null && alreadyUsedSkills.Contains(skillName, StringComparer.OrdinalIgnoreCase);

        var t2Skills = match.ImplicitlyDiscoveredSkills.Select(s => s.SkillName).Where(s => !IsUsed(s)).ToList();
        var t3Skills = match.PrerequisiteMetSkills.Where(s => !IsUsed(s.OriginalName ?? s.SkillName)).ToList();
        var t4Skills = match.BridgeableSkills.Where(s => !IsUsed(s.OriginalName ?? s.SkillName)).ToList();

        // Sec B takes priority: remove from Sec C any target already covered by Sec B
        // to prevent within-entry duplication where the same skill gets both a B and C bullet.
        var t3TargetNames = new HashSet<string>(
            t3Skills.Select(s => s.OriginalName ?? s.SkillName),
            StringComparer.OrdinalIgnoreCase);
        t4Skills = t4Skills
            .Where(s => !t3TargetNames.Contains(s.OriginalName ?? s.SkillName))
            .ToList();

        var hasT2 = t2Skills.Any();
        var hasT3 = t3Skills.Any();
        var hasT4 = t4Skills.Any();
        var hasT5 = match.HardGaps.Any();

        if (!hasT2 && !hasT3 && !hasT4)
            return string.Empty;

        // Format a source skill entry: "SkillName (5 yr)" or "SkillName (inferred)"
        string SourceLabel(string canonicalSource)
        {
            var display = canonicalToDisplay.TryGetValue(canonicalSource, out var d) ? d : canonicalSource;
            if (documentedSkills.Contains(canonicalSource))
            {
                var yr = canonicalToYears.TryGetValue(canonicalSource, out var y) && y > 0
                    ? $"{y:0.#} yr"
                    : "documented";
                return $"{display} ({yr})";
            }
            return $"{display} (inferred)";
        }

        // Graph context is pure data — no embedded rules.
        // All instructions are in ResumeTailoring.md, which the LLM reads before this block.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("════════════════════════════════════════");
        sb.AppendLine("KNOWLEDGE GRAPH");
        sb.AppendLine("════════════════════════════════════════");

        if (hasT2)
        {
            sb.AppendLine();
            sb.AppendLine("SECTION A — CLAIM DIRECTLY:");
            foreach (var skill in t2Skills)
            {
                var display = canonicalToDisplay.TryGetValue(skill, out var d) ? d : skill;
                var note = documentedSkills.Contains(skill)
                    ? (canonicalToYears.TryGetValue(skill, out var y) && y > 0 ? $"{y:0.#} yr" : "documented")
                    : "inferred";
                sb.AppendLine($"  • {display} ({note})");
            }
        }

        if (hasT3)
        {
            sb.AppendLine();
            sb.AppendLine("SECTION B — PREREQUISITE → SPECIALIZATION:");
            foreach (var skill in t3Skills)
            {
                var target = skill.OriginalName ?? skill.SkillName;
                var fromCanonical = ParseViaSkill(skill.BridgePath) ?? "your foundation";
                sb.AppendLine($"  • {target}  ←  {SourceLabel(fromCanonical)}");
            }
        }

        if (hasT4)
        {
            sb.AppendLine();
            sb.AppendLine("SECTION C — ADJACENT → BRIDGE:");
            foreach (var skill in t4Skills)
            {
                var target = skill.OriginalName ?? skill.SkillName;
                var fromCanonical = ParseViaSkill(skill.BridgePath) ?? "your domain experience";
                sb.AppendLine($"  • {target}  ←  {SourceLabel(fromCanonical)}");
            }
        }

        if (hasT5)
        {
            sb.AppendLine();
            sb.AppendLine("OFF LIMITS — never mention:");
            var hardGapNames = match.HardGaps
                .Select(s => s.OriginalName ?? s.SkillName)
                .ToList();
            sb.AppendLine("  " + string.Join(", ", hardGapNames));
        }

        sb.AppendLine();
        sb.AppendLine("════════════════════════════════════════");

        return sb.ToString();
    }

    /// <summary>
    /// Extracts the ViaSkill name from a BridgePath string formatted as "via X (TYPE)".
    /// Returns null if the path is null or cannot be parsed.
    /// </summary>
    private static string? ParseViaSkill(string? bridgePath)
    {
        if (string.IsNullOrWhiteSpace(bridgePath))
            return null;

        // Expected format: "via React.js (SUBSET_OF)"
        // Strip leading "via " prefix, then strip trailing " (TYPE)" suffix.
        var s = bridgePath.Trim();
        if (s.StartsWith("via ", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(4).Trim();

        var parenIdx = s.LastIndexOf(" (", StringComparison.Ordinal);
        if (parenIdx > 0)
            s = s.Substring(0, parenIdx).Trim();

        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public void Dispose()
    {
        _driver?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_driver != null)
        {
            await _driver.DisposeAsync();
        }
    }
}
