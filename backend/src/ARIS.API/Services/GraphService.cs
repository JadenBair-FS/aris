using Neo4j.Driver;

namespace ARIS.API.Services;

public class GraphService : IDisposable, IAsyncDisposable
{
    private readonly IDriver _driver;
    private readonly ILogger<GraphService> _logger;

    public GraphService(IConfiguration configuration, ILogger<GraphService> logger)
    {
        _logger = logger;
        var uri = "bolt://localhost:7687";
        var user = "neo4j";
        var password = "aris_password_local";

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
    /// Retrieves the Valid Neighborhood for a user based on known skills.
    /// V = S_user U N_k(S_user) where k=2.
    /// When includeTechSkills is false, only Roadmap.sh-sourced tech skills are excluded —
    /// domain tool bridges (CRM, EHR, etc.) that carry is_tech=true but source='ONET_Skill' are allowed.
    /// </summary>
    public async Task<HashSet<string>> GetValidNeighborhoodAsync(IEnumerable<string> userSkills, bool includeTechSkills = true)
    {
        var validSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expansionSeed = userSkills.Where(s => !UniversalSkills.Contains(s)).ToList();

        foreach (var skill in userSkills)
        {
            validSkills.Add(skill);
        }

        // A2: Filter changed from `NOT coalesce(x.is_tech, false)` to
        //     `NOT (coalesce(x.is_tech, false) AND x.source = 'Roadmap.sh')`
        // so that domain tool bridges (Salesforce, EHR, CRM) are allowed for non-tech candidates
        // while programming/engineering skills from Roadmap.sh are still blocked.
        // A1: Removed non-existent RELATED_TO relationship type from lateral traversal.
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
                var cursor = await tx.RunAsync(query, new { expansionSeed, universal = UniversalSkills.ToList(), includeTech = includeTechSkills });
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
        var expansionSeed = userSkills.Where(s => !UniversalSkills.Contains(s)).ToList();

        // A2: Updated filter to only block Roadmap.sh-sourced tech skills.
        const string query = @"
            // Direction 1: child -[:SUBSET_OF*1..3]-> parent (original — parent in user skills)
            MATCH (parent:Skill)
            WHERE toLower(parent.name) IN [s IN $expansionSeed | toLower(s)]
            MATCH (child:Skill)-[:SUBSET_OF*1..3]->(parent)
            WHERE toLower(child.name) IN [s IN $missingSkills | toLower(s)]
              AND ($includeTech OR NOT (coalesce(child.is_tech, false) AND child.source = 'Roadmap.sh'))
              AND NOT toLower(child.name) IN [u IN $universal | toLower(u)]
            RETURN DISTINCT child.name AS Name

            UNION

            // Direction 2: user skill -[:SUBSET_OF*1..3]-> missing skill (roadmap direction — user has foundation)
            MATCH (foundation:Skill)
            WHERE toLower(foundation.name) IN [s IN $expansionSeed | toLower(s)]
            MATCH (foundation)-[:SUBSET_OF*1..3]->(target:Skill)
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
                var cursor = await tx.RunAsync(query, new { expansionSeed, missingSkills = missingSkills.ToList(), universal = UniversalSkills.ToList(), includeTech = includeTechSkills });
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
    /// Returns skills that are implicitly granted because the user knows a child specialization
    /// (UP traversal: child → parent via SUBSET_OF), plus 1-hop BRIDGE_TO lateral coverage.
    ///
    /// A3: Removed the DOWN block (parent → children) — that direction belongs in
    /// GetPrerequisiteMetSkillsAsync ("Prerequisite Met") not here ("Implicitly Matched").
    /// A1: Removed non-existent IS_PARENT_OF and RELATED_TO relationship types.
    /// A2: Updated filter to only block Roadmap.sh-sourced tech skills.
    /// </summary>
    public async Task<HashSet<string>> GetImplicitlyDiscoveredSkillsAsync(IEnumerable<string> userSkills, bool includeTechSkills = true)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expansionSeed = userSkills.Where(s => !UniversalSkills.Contains(s)).ToList();

        const string query = @"
            // UP: child -> parent (user knows specialization → implicitly knows foundation)
            MATCH (child:Skill)
            WHERE toLower(child.name) IN [s IN $expansionSeed | toLower(s)]
            MATCH (child)-[:SUBSET_OF*1..3]->(parent:Skill)
            WHERE ($includeTech OR NOT (coalesce(parent.is_tech, false) AND parent.source = 'Roadmap.sh'))
              AND NOT toLower(parent.name) IN [u IN $universal | toLower(u)]
            RETURN DISTINCT parent.name AS Name

            UNION

            // LATERAL: Bridge (1 hop) — strong adjacent coverage grants implicit recognition
            MATCH (s:Skill)
            WHERE toLower(s.name) IN [term IN $expansionSeed | toLower(term)]
            MATCH (s)-[:BRIDGE_TO]-(neighbor:Skill)
            WHERE ($includeTech OR NOT (coalesce(neighbor.is_tech, false) AND neighbor.source = 'Roadmap.sh'))
              AND NOT toLower(neighbor.name) IN [u IN $universal | toLower(u)]
            RETURN DISTINCT neighbor.name AS Name
        ";

        try
        {
            await using var session = _driver.AsyncSession();
            var records = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(query, new { expansionSeed, universal = UniversalSkills.ToList(), includeTech = includeTechSkills });
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
    /// B2: Returns bridge path data for a set of missing skills reachable from user skills.
    /// Used by MatchService to enrich SkillGapItem objects with BridgePath/BridgeSource context.
    /// </summary>
    public async Task<List<(string SkillName, string ViaSkill, string BridgeType, string? BridgeSource)>> GetBridgeablePathsAsync(
        IEnumerable<string> userSkills, IEnumerable<string> missingSkills)
    {
        var result = new List<(string, string, string, string?)>();
        var userList = userSkills.Where(s => !UniversalSkills.Contains(s)).ToList();
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
    /// B3: Serializes the most relevant graph paths between user skills and job skills
    /// into a structured string for use as LLM grounding context.
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
