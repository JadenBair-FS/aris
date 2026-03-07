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
            await tx.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (r:Role) REQUIRE r.onet_code IS UNIQUE");
            await tx.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (s:Skill) REQUIRE s.name IS UNIQUE");
            await tx.RunAsync("CREATE INDEX IF NOT EXISTS FOR (s:Skill) ON (s.name)");
            await tx.RunAsync("CREATE INDEX IF NOT EXISTS FOR (s:Skill) ON (s.source)");
        });
    }

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

        const string query = @"
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
        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { skillA, skillB, source }));
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
