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

    public static readonly HashSet<string> UniversalSkills = new(StringComparer.OrdinalIgnoreCase)
    {
        // Generic O*NET soft skills / management competencies
        "Writing", "Management of Personnel Resources", "Service Orientation",
        "Operations Analysis", "Social Perceptiveness", "Negotiation",
        "Complex Problem Solving", "Time Management", "Active Listening",
        "Critical Thinking", "Coordination", "Instructing", "Speaking",
        "Management of Material Resources", "Management of Financial Resources",
        "Science", "Judgment and Decision Making", "Monitoring", "Persuasion",
        "Operation and Control", "Equipment Maintenance", "Repairing", "Equipment Selection",
        "Systems Analysis", "Systems Evaluation", "Active Learning",
        // Broad O*NET knowledge categories — too generic to be meaningful intermediate bridge hops.
        // A non-medical candidate having 'Biology' in their expanded neighborhood should NOT
        // count as a stepping-stone to specialized EHR/OR-suite software. These terms still
        // function as bridge seeds for users who have them EXPLICITLY in their CleanSignal skills.
        "Physics", "Biology", "Chemistry", "Mathematics",
        "Computers and Electronics", "Hardware knowledge",
        "Engineering and Technology", "Life, Physical, and Social Science",
        // Additional O*NET general-use categories that were still acting as cross-domain hops
        // after the first round of filtering (e.g. "Working with Computers" → EHR software,
        // "Medicine and Dentistry" → surgical tools for non-clinical candidates).
        "Working with Computers", "Medicine and Dentistry", "Algebra"
    };

    /// <summary>
    /// Returns the valid skill neighborhood: the user's known skills plus all reachable neighbors
    /// within 2 hops via SUBSET_OF (up and down) and BRIDGE_TO edges.
    /// When includeTechSkills is false, only Roadmap.sh-sourced tech skills are excluded —
    /// domain tool bridges (CRM, EHR, etc.) that carry is_tech=true but source='ONET_Skill' are still allowed.
    /// </summary>
    public async Task<HashSet<string>> GetValidNeighborhoodAsync(IEnumerable<string> userSkills, bool includeTechSkills = true, bool skipUniversalFilter = false)
    {
        var validSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var universalList = skipUniversalFilter ? new List<string>() : UniversalSkills.ToList();
        var expansionSeed = skipUniversalFilter
            ? userSkills.ToList()
            : userSkills.Where(s => !UniversalSkills.Contains(s)).ToList();

        foreach (var skill in userSkills)
        {
            validSkills.Add(skill);
        }

        const string query = @"
            MATCH (s:Skill)
            WHERE toLower(s.name) IN [term IN $expansionSeed | toLower(term)]
            CALL {
                WITH s
                // 1. Hierarchical UP: s -> parent (Foundations)
                MATCH (s)-[:SUBSET_OF*1..2]->(parent:Skill)
                WHERE ($includeTech OR NOT (coalesce(parent.is_tech, false) AND parent.source = 'Roadmap.sh'))
                  AND NOT toLower(parent.name) IN [u IN $universal | toLower(u)]
                RETURN parent.name as Name
                UNION
                // 2. Hierarchical DOWN: child -> s (s is a parent/foundation)
                MATCH (child:Skill)-[:SUBSET_OF*1..2]->(s)
                WHERE ($includeTech OR NOT (coalesce(child.is_tech, false) AND child.source = 'Roadmap.sh'))
                  AND NOT toLower(child.name) IN [u IN $universal | toLower(u)]
                RETURN child.name as Name
                UNION
                // 3. Peer/Bridge traversal (Lateral) - 1 hop only to prevent domain leakage
                MATCH (s)-[:BRIDGE_TO]-(neighbor:Skill)
                WHERE ($includeTech OR NOT (coalesce(neighbor.is_tech, false) AND neighbor.source = 'Roadmap.sh'))
                  AND NOT toLower(neighbor.name) IN [u IN $universal | toLower(u)]
                RETURN neighbor.name as Name
            }
            RETURN DISTINCT Name
        ";

        try
        {
            await using var session = _driver.AsyncSession();
            var result = await session.ExecuteReadAsync(async tx => {
                var cursor = await tx.RunAsync(query, new { expansionSeed, universal = universalList, includeTech = includeTechSkills });
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

    public async Task<HashSet<string>> GetPrerequisiteMetSkillsAsync(IEnumerable<string> userSkills, IEnumerable<string> missingSkills, bool includeTechSkills = true, bool skipUniversalFilter = false)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var universalList = skipUniversalFilter ? new List<string>() : UniversalSkills.ToList();
        var expansionSeed = skipUniversalFilter
            ? userSkills.ToList()
            : userSkills.Where(s => !UniversalSkills.Contains(s)).ToList();

        const string query = @"
            // Direction 1: job skill -[:SUBSET_OF*1..2]-> user skill (user has the parent foundation)
            // e.g., user has 'JavaScript', job needs 'React' (React SUBSET_OF JavaScript).
            MATCH (parent:Skill)
            WHERE toLower(parent.name) IN [s IN $expansionSeed | toLower(s)]
            MATCH (child:Skill)-[:SUBSET_OF*1..2]->(parent)
            WHERE toLower(child.name) IN [s IN $missingSkills | toLower(s)]
              AND ($includeTech OR NOT (coalesce(child.is_tech, false) AND child.source = 'Roadmap.sh'))
              AND NOT toLower(child.name) IN [u IN $universal | toLower(u)]
            RETURN DISTINCT child.name AS Name

            UNION

            // Direction 2: user skill -[:SUBSET_OF*1..2]-> job skill (user's skill is a specialization of the requirement)
            // e.g., user has 'React', job needs 'JavaScript'.
            MATCH (foundation:Skill)
            WHERE toLower(foundation.name) IN [s IN $expansionSeed | toLower(s)]
            MATCH (foundation)-[:SUBSET_OF*1..2]->(target:Skill)
            WHERE toLower(target.name) IN [s IN $missingSkills | toLower(s)]
              AND ($includeTech OR NOT (coalesce(target.is_tech, false) AND target.source = 'Roadmap.sh'))
              AND NOT toLower(target.name) IN [u IN $universal | toLower(u)]
            RETURN DISTINCT target.name AS Name
        ";

        try
        {
            await using var session = _driver.AsyncSession();
            var records = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(query, new { expansionSeed, missingSkills = missingSkills.ToList(), universal = universalList, includeTech = includeTechSkills });
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
    /// BRIDGE_TO neighbors belong to Tier 4 (Bridgeable) and are excluded here.
    /// DOWN traversal (parent → children) belongs in GetPrerequisiteMetSkillsAsync.
    /// </summary>
    public async Task<HashSet<string>> GetImplicitlyDiscoveredSkillsAsync(IEnumerable<string> userSkills, bool includeTechSkills = true, bool skipUniversalFilter = false)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var universalList = skipUniversalFilter ? new List<string>() : UniversalSkills.ToList();
        var expansionSeed = skipUniversalFilter
            ? userSkills.ToList()
            : userSkills.Where(s => !UniversalSkills.Contains(s)).ToList();

        const string query = @"
            // UP: child -> parent (user knows specialization → implicitly knows foundation)
            // 2-hop limit: consistent with the thesis claim and GetValidNeighborhoodAsync.
            MATCH (child:Skill)
            WHERE toLower(child.name) IN [s IN $expansionSeed | toLower(s)]
            MATCH (child)-[:SUBSET_OF*1..2]->(parent:Skill)
            WHERE ($includeTech OR NOT (coalesce(parent.is_tech, false) AND parent.source = 'Roadmap.sh'))
              AND NOT toLower(parent.name) IN [u IN $universal | toLower(u)]
            RETURN DISTINCT parent.name AS Name
        ";

        try
        {
            await using var session = _driver.AsyncSession();
            var records = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(query, new { expansionSeed, universal = universalList, includeTech = includeTechSkills });
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
            _logger.LogError(ex, "Failed to retrieve implicit parent skills.");
        }

        return result;
    }

    /// <summary>
    /// Returns bridge path data for missing skills reachable from user skills via BRIDGE_TO or SUBSET_OF edges.
    /// Used by MatchService to enrich SkillGapItem objects with BridgePath and BridgeSource context.
    /// </summary>
    public async Task<List<(string SkillName, string ViaSkill, string BridgeType, string? BridgeSource)>> GetBridgeablePathsAsync(
        IEnumerable<string> userSkills, IEnumerable<string> missingSkills, bool skipUniversalFilter = false)
    {
        var result = new List<(string, string, string, string?)>();
        var userList = skipUniversalFilter
            ? userSkills.ToList()
            : userSkills.Where(s => !UniversalSkills.Contains(s)).ToList();
        var missingList = missingSkills.ToList();

        if (userList.Count == 0 || missingList.Count == 0)
            return result;

        const string bridgeQuery = @"
            MATCH (u:Skill)-[r:BRIDGE_TO]-(missing:Skill)
            WHERE toLower(u.name) IN [s IN $userSkills | toLower(s)]
              AND toLower(missing.name) IN [s IN $missingSkills | toLower(s)]
            RETURN missing.name AS SkillName, u.name AS ViaSkill, 'BRIDGE_TO' AS BridgeType, r.source AS BridgeSource
        ";

        const string subsetQuery = @"
            MATCH (u:Skill)-[:SUBSET_OF*1..3]-(missing:Skill)
            WHERE toLower(u.name) IN [s IN $userSkills | toLower(s)]
              AND toLower(missing.name) IN [s IN $missingSkills | toLower(s)]
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
