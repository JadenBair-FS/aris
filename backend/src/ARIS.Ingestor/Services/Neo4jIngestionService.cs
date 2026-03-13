using Neo4j.Driver;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ARIS.Shared.Models;

namespace ARIS.Ingestor.Services;

public class Neo4jIngestionService : IDisposable, IAsyncDisposable
{
    private readonly IDriver _driver;
    private readonly ILogger<Neo4jIngestionService> _logger;

    public Neo4jIngestionService(IConfiguration configuration, ILogger<Neo4jIngestionService> logger)
    {
        _logger = logger;
        var uri = "bolt://localhost:7687";
        var user = "neo4j";
        var password = "aris_password_local";
        
        _logger.LogInformation("Connecting to Neo4j at {Uri} as user {User}", uri, user);
        _driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
    }

    public async Task ClearDatabaseAsync()
    {
        await using var session = _driver.AsyncSession();
        await session.RunAsync(
            "MATCH (n) CALL { WITH n DETACH DELETE n } IN TRANSACTIONS OF 1000 ROWS");
        _logger.LogInformation("Neo4j Database Cleared.");
    }

    public async Task EnsureIndicesAsync()
    {
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Existing
            await tx.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (r:Role) REQUIRE r.onet_code IS UNIQUE");
            await tx.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (s:Skill) REQUIRE s.name IS UNIQUE");
            await tx.RunAsync("CREATE INDEX IF NOT EXISTS FOR (s:Skill) ON (s.name)");
            await tx.RunAsync("CREATE INDEX IF NOT EXISTS FOR (s:Skill) ON (s.source)");
            // New node labels
            await tx.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (k:Knowledge) REQUIRE k.onet_id IS UNIQUE");
            await tx.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (a:Ability) REQUIRE a.onet_id IS UNIQUE");
            await tx.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (t:Task) REQUIRE t.onet_id IS UNIQUE");
            await tx.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (w:WorkActivity) REQUIRE w.onet_id IS UNIQUE");
            await tx.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (j:JobZone) REQUIRE j.code IS UNIQUE");
            await tx.RunAsync("CREATE INDEX IF NOT EXISTS FOR (k:Knowledge) ON (k.name)");
            await tx.RunAsync("CREATE INDEX IF NOT EXISTS FOR (a:Ability) ON (a.name)");
            await tx.RunAsync("CREATE INDEX IF NOT EXISTS FOR (w:WorkActivity) ON (w.name)");
            // IS_SIMILAR_TO edges are written by the BERT post-ingestion script — no constraint needed
        });
    }

    // --- Knowledge ---

    public async Task UpsertKnowledgeNodeAsync(string onetId, string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(onetId) || string.IsNullOrWhiteSpace(name)) return;
        const string query = @"
            MERGE (k:Knowledge {onet_id: $onetId})
            SET k.name = $name, k.description = $description, k.updated_at = datetime()
        ";
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { onetId, name, description }));
    }

    public async Task LinkRoleToKnowledgeAsync(string roleOnetCode, string knowledgeOnetId, int? importance)
    {
        if (string.IsNullOrWhiteSpace(roleOnetCode) || string.IsNullOrWhiteSpace(knowledgeOnetId)) return;
        const string query = @"
            MATCH (r:Role {onet_code: $code})
            MATCH (k:Knowledge {onet_id: $kid})
            MERGE (r)-[rel:REQUIRES_KNOWLEDGE]->(k)
            SET rel.importance = $importance
        ";
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { code = roleOnetCode, kid = knowledgeOnetId, importance }));
    }

    // --- Ability ---

    public async Task UpsertAbilityNodeAsync(string onetId, string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(onetId) || string.IsNullOrWhiteSpace(name)) return;
        const string query = @"
            MERGE (a:Ability {onet_id: $onetId})
            SET a.name = $name, a.description = $description, a.updated_at = datetime()
        ";
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { onetId, name, description }));
    }

    public async Task LinkRoleToAbilityAsync(string roleOnetCode, string abilityOnetId, int? importance)
    {
        if (string.IsNullOrWhiteSpace(roleOnetCode) || string.IsNullOrWhiteSpace(abilityOnetId)) return;
        const string query = @"
            MATCH (r:Role {onet_code: $code})
            MATCH (a:Ability {onet_id: $aid})
            MERGE (r)-[rel:REQUIRES_ABILITY]->(a)
            SET rel.importance = $importance
        ";
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { code = roleOnetCode, aid = abilityOnetId, importance }));
    }

    // --- Task ---

    public async Task UpsertTaskNodeAsync(string onetId, string statement, int? importance)
    {
        if (string.IsNullOrWhiteSpace(onetId) || string.IsNullOrWhiteSpace(statement)) return;
        const string query = @"
            MERGE (t:Task {onet_id: $onetId})
            SET t.statement = $statement, t.importance = $importance, t.updated_at = datetime()
        ";
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { onetId, statement, importance }));
    }

    public async Task LinkRoleToTaskAsync(string roleOnetCode, string taskOnetId, int? importance)
    {
        if (string.IsNullOrWhiteSpace(roleOnetCode) || string.IsNullOrWhiteSpace(taskOnetId)) return;
        const string query = @"
            MATCH (r:Role {onet_code: $code})
            MATCH (t:Task {onet_id: $tid})
            MERGE (r)-[rel:INVOLVES_TASK]->(t)
            SET rel.importance = $importance
        ";
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { code = roleOnetCode, tid = taskOnetId, importance }));
    }

    // --- WorkActivity ---

    public async Task UpsertWorkActivityNodeAsync(string onetId, string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(onetId) || string.IsNullOrWhiteSpace(name)) return;
        const string query = @"
            MERGE (w:WorkActivity {onet_id: $onetId})
            SET w.name = $name, w.description = $description, w.updated_at = datetime()
        ";
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { onetId, name, description }));
    }

    public async Task LinkRoleToWorkActivityAsync(string roleOnetCode, string workActivityOnetId, int? importance)
    {
        if (string.IsNullOrWhiteSpace(roleOnetCode) || string.IsNullOrWhiteSpace(workActivityOnetId)) return;
        const string query = @"
            MATCH (r:Role {onet_code: $code})
            MATCH (w:WorkActivity {onet_id: $wid})
            MERGE (r)-[rel:INVOLVES_ACTIVITY]->(w)
            SET rel.importance = $importance
        ";
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { code = roleOnetCode, wid = workActivityOnetId, importance }));
    }

    // --- JobZone ---

    public async Task UpsertJobZoneNodeAsync(int code, string title)
    {
        const string query = @"
            MERGE (j:JobZone {code: $code})
            SET j.title = $title
        ";
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { code, title }));
    }

    public async Task LinkRoleToJobZoneAsync(string roleOnetCode, int jobZoneCode)
    {
        if (string.IsNullOrWhiteSpace(roleOnetCode)) return;
        const string query = @"
            MATCH (r:Role {onet_code: $code})
            MATCH (j:JobZone {code: $jzCode})
            MERGE (r)-[:BELONGS_TO_ZONE]->(j)
        ";
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { code = roleOnetCode, jzCode = jobZoneCode }));
    }

    // --- Roadmap.sh node type helpers ---

    /// <summary>
    /// Returns true if this roadmap node type represents an actual tool or framework name
    /// that should be ingested as a Skill node. Only "subtopic" nodes contain real tool names.
    /// </summary>
    public static bool IsSkillNode(string nodeType) =>
        string.Equals(nodeType, "subtopic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if this roadmap node type is a structural/layout node that should
    /// be ignored entirely (no Skill node, no relationships).
    /// </summary>
    public static bool IsStructuralNode(string nodeType) =>
        nodeType is "vertical" or "horizontal" or "section" or "label"
                 or "button" or "paragraph" or "linksgroup" or "legend"
                 or "title";

    public async Task MergeRoleAsync(string title, string code, string description)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            _logger.LogWarning("Skipping MergeRole: O*NET Code is null or empty. Title={Title}", title);
            return;
        }

        const string query = @"
            MERGE (r:Role {onet_code: $code})
            ON CREATE SET r.title = $title, r.description = $description, r.created_at = datetime()
            ON MATCH SET r.title = $title, r.updated_at = datetime()
        ";

        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { code, title, description }));
    }

    public async Task MergeSkillAsync(string name, string source, bool isTech = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            _logger.LogWarning("Skipping MergeSkill: Name is null or empty. Source={Source}", source);
            return;
        }

        // ON MATCH: upgrade is_tech if this call asserts it (true wins, never downgrade)
        const string query = @"
            MERGE (s:Skill {name: $name})
            ON CREATE SET s.source = $source, s.is_tech = $isTech, s.created_at = datetime()
            ON MATCH SET s.source = CASE WHEN $isTech AND NOT coalesce(s.is_tech, false) THEN $source ELSE s.source END,
                         s.is_tech = CASE WHEN $isTech THEN true ELSE coalesce(s.is_tech, false) END
        ";

        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { name, source, isTech }));
    }

    public async Task SetSkillSourceAsync(string skillName, string source)
    {
        if (string.IsNullOrWhiteSpace(skillName)) return;

        const string query = @"
            MATCH (s:Skill {name: $name})
            SET s.source = $source
        ";

        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { name = skillName, source }));
    }

    public async Task SetSkillIsTechAsync(string skillName, bool isTech = true)
    {
        if (string.IsNullOrWhiteSpace(skillName)) return;

        const string query = @"
            MATCH (s:Skill {name: $name})
            SET s.is_tech = $isTech
        ";

        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { name = skillName, isTech }));
    }

    public async Task MergeRoleSkillRelationshipAsync(string roleCode, string skillName, string relationshipType = "REQUIRES")
    {        
        if (string.IsNullOrWhiteSpace(roleCode) || string.IsNullOrWhiteSpace(skillName))
        {
            _logger.LogWarning("Skipping Role-Skill relationship: role='{Role}', skill='{Skill}' (Null or empty)", roleCode, skillName);
            return;
        }

        const string query = @"
            MATCH (r:Role {onet_code: $roleCode})
            MATCH (s:Skill {name: $skillName})
            MERGE (r)-[rel:REQUIRES]->(s)
            ON CREATE SET rel.created_at = datetime()
        ";

        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { roleCode, skillName }));
    }

    public async Task MergeSubsetRelationshipAsync(string childName, string parentName, string source = "Roadmap.sh")
    {
        if (string.IsNullOrWhiteSpace(childName) || string.IsNullOrWhiteSpace(parentName))
        {
            _logger.LogWarning("Skipping SUBSET_OF relationship: child='{Child}', parent='{Parent}' (Null or empty)", childName, parentName);
            return;
        }

        // This query creates the SUBSET_OF relationship and removes any existing
        // BRIDGE_TO relationship between the same two nodes, as the hierarchy supersedes the bridge.
        // ON MATCH: never downgrade source, but ensure it is not left NULL.
        const string query = @"
            MERGE (c:Skill {name: $childName})
            ON CREATE SET c.source = $source, c.created_at = datetime()
            ON MATCH SET c.source = coalesce(c.source, $source)
            WITH c
            MERGE (p:Skill {name: $parentName})
            ON CREATE SET p.source = $source, p.created_at = datetime()
            ON MATCH SET p.source = coalesce(p.source, $source)
            MERGE (c)-[rel:SUBSET_OF]->(p)
            ON CREATE SET rel.confidence = 0.9, rel.source = $source, rel.created_at = datetime()
            ON MATCH SET rel.source = coalesce(rel.source, $source)
            WITH c, p
            OPTIONAL MATCH (c)-[b:BRIDGE_TO]-(p)
            DELETE b
        ";

        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { childName, parentName, source }));
    }

    public async Task MergeBridgeRelationshipAsync(string skillA, string skillB, string source = "Roadmap.sh")
    {
        if (string.IsNullOrWhiteSpace(skillA) || string.IsNullOrWhiteSpace(skillB))
        {
            _logger.LogWarning("Skipping BRIDGE_TO relationship: skillA='{SkillA}', skillB='{SkillB}' (Null or empty)", skillA, skillB);
            return;
        }

        // ONET_Category bridges: create nodes if they don't exist (deterministic data)
        // OntologyEnrichment / Roadmap.sh bridges: only link existing nodes —
        // hallucinated names from the LLM must not create orphaned Skill nodes.
        if (source == "ONET_Category")
        {
            const string createQuery = @"
                MERGE (a:Skill {name: $skillA})
                ON CREATE SET a.source = $source, a.created_at = datetime()
                ON MATCH SET a.source = coalesce(a.source, $source)
                WITH a
                MERGE (b:Skill {name: $skillB})
                ON CREATE SET b.source = $source, b.created_at = datetime()
                ON MATCH SET b.source = coalesce(b.source, $source)
                MERGE (a)-[rel:BRIDGE_TO]-(b)
                ON CREATE SET rel.confidence = 0.9, rel.source = $source, rel.created_at = datetime()
                ON MATCH SET rel.source = coalesce(rel.source, $source)
            ";
            await using var session = _driver.AsyncSession();
            await session.ExecuteWriteAsync(tx => tx.RunAsync(createQuery, new { skillA, skillB, source }));
        }
        else
        {
            // Safe path: only create the edge if BOTH nodes already exist in the graph.
            // If either name was hallucinated by the LLM it simply produces no edge.
            const string matchQuery = @"
                MATCH (a:Skill {name: $skillA})
                MATCH (b:Skill {name: $skillB})
                MERGE (a)-[rel:BRIDGE_TO]-(b)
                ON CREATE SET rel.confidence = 0.9, rel.source = $source, rel.created_at = datetime()
                ON MATCH SET rel.source = coalesce(rel.source, $source)
            ";
            await using var session = _driver.AsyncSession();
            await session.ExecuteWriteAsync(tx => tx.RunAsync(matchQuery, new { skillA, skillB, source }));
        }
    }

    /// <summary>
    /// Returns all existing BRIDGE_TO pairs as normalized keys ("skillA|skillB", alphabetically sorted)
    /// for pre-populating the dedup set in --optimize-graph runs.
    /// </summary>
    public async Task<HashSet<string>> GetExistingBridgeKeysAsync()
    {
        const string query = @"
            MATCH (a:Skill)-[:BRIDGE_TO]-(b:Skill)
            WHERE a.name < b.name
            RETURN a.name AS SkillA, b.name AS SkillB
        ";

        await using var session = _driver.AsyncSession();
        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query);
            var records = await cursor.ToListAsync();
            return records
                .Select(r => $"{r["SkillA"].As<string>()}|{r["SkillB"].As<string>()}")
                .ToHashSet();
        });

        return result;
    }

    public async Task DeleteBridgesAsync()
    {
        const string query = "MATCH ()-[r:BRIDGE_TO]-() DELETE r";
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query));
        _logger.LogInformation("Deleted all existing BRIDGE_TO relationships.");
    }

    public async Task DeleteSubsetRelationshipsAsync()
    {
        const string query = "MATCH ()-[r:SUBSET_OF]-() DELETE r";
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query));
        _logger.LogInformation("Deleted all existing SUBSET_OF relationships.");
    }

    public async Task<List<SiblingCluster>> GetSiblingClustersAsync()
    {
        const string query = @"
            MATCH (c)-[:SUBSET_OF]->(p)
            RETURN p.name as Parent, collect(c.name) as Siblings
        ";

        await using var session = _driver.AsyncSession();
        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query);
            var records = await cursor.ToListAsync();
            return records.Select(r => new SiblingCluster
            {
                Parent = r["Parent"].As<string>(),
                Siblings = r["Siblings"].As<List<string>>()
            }).ToList();
        });

        return result;
    }

    public async Task<List<SiblingCluster>> GetRoadmapSiblingClustersAsync()
    {
        const string query = @"
            MATCH (c:Skill {source: 'Roadmap.sh'})-[:SUBSET_OF]->(p)
            RETURN p.name AS Parent, collect(c.name) AS Siblings
        ";

        await using var session = _driver.AsyncSession();
        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query);
            var records = await cursor.ToListAsync();
            return records.Select(r => new SiblingCluster
            {
                Parent = r["Parent"].As<string>(),
                Siblings = r["Siblings"].As<List<string>>()
            }).ToList();
        });

        return result;
    }

    public async Task<List<RoleSkillCluster>> GetRolesWithSkillsAsync()
    {
        const string query = @"
            MATCH (r:Role)-[:REQUIRES]->(direct:Skill)
            OPTIONAL MATCH (child:Skill)-[:SUBSET_OF*1..]->(direct)
            WITH r, collect(DISTINCT direct.name) + collect(DISTINCT child.name) AS skills
            RETURN r.title AS RoleTitle, skills AS Skills
            ORDER BY r.title
        ";

        await using var session = _driver.AsyncSession();
        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query);
            var records = await cursor.ToListAsync();
            return records.Select(r => new RoleSkillCluster
            {
                RoleTitle = r["RoleTitle"].As<string>(),
                Skills = r["Skills"].As<List<string>>()
            }).ToList();
        });

        return result;
    }

    /// <summary>
    /// Returns all roles with only their direct REQUIRES skills (no transitive SUBSET_OF expansion).
    /// Use this for LLM-based analysis (dependency detection, role bridge detection) to avoid
    /// context-window overflow from recursive skill tree expansion.
    /// </summary>
    public async Task<List<RoleSkillCluster>> GetRolesWithDirectSkillsAsync()
    {
        const string query = @"
            MATCH (r:Role)-[:REQUIRES]->(s:Skill)
            WITH r, collect(DISTINCT s.name) AS skills
            RETURN r.title AS RoleTitle, skills AS Skills
            ORDER BY r.title
        ";

        await using var session = _driver.AsyncSession();
        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query);
            var records = await cursor.ToListAsync();
            return records.Select(r => new RoleSkillCluster
            {
                RoleTitle = r["RoleTitle"].As<string>(),
                Skills = r["Skills"].As<List<string>>()
            }).ToList();
        });

        return result;
    }

    /// <summary>
    /// Returns pure O*NET roles (no Roadmap.sh requirements) with only their is_tech=true
    /// domain-specific tools. These are the roles eligible for domain-tool bridge detection.
    /// </summary>
    public async Task<List<RoleSkillCluster>> GetDomainToolRolesAsync()
    {
        const string query = @"
            MATCH (r:Role)-[:REQUIRES]->(s:Skill {is_tech: true})
            WHERE NOT EXISTS {
                MATCH (r)-[:REQUIRES]->(rs:Skill {source: 'Roadmap.sh'})
            }
            WITH r, collect(DISTINCT s.name) AS domain_tools
            WHERE size(domain_tools) >= 2
            RETURN r.title AS RoleTitle, domain_tools AS Skills
            ORDER BY r.title
        ";

        await using var session = _driver.AsyncSession();
        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query);
            var records = await cursor.ToListAsync();
            return records.Select(r => new RoleSkillCluster
            {
                RoleTitle = r["RoleTitle"].As<string>(),
                Skills = r["Skills"].As<List<string>>()
            }).ToList();
        });

        return result;
    }

    public async Task<List<(string Child, string Parent)>> GetInternalHierarchiesAsync(List<string> skills)
    {
        const string query = @"
            MATCH (c:Skill)-[:SUBSET_OF*1..4]->(p:Skill)
            WHERE c.name IN $skills AND p.name IN $skills
            RETURN c.name as Child, p.name as Parent
        ";

        await using var session = _driver.AsyncSession();
        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query, new { skills });
            var records = await cursor.ToListAsync();
            return records.Select(r => (r["Child"].As<string>(), r["Parent"].As<string>())).ToList();
        });

        return result;
    }

    public IDriver GetDriver() => _driver;

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
