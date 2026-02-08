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

    /// <summary>
    /// Retrieves the Valid Neighborhood for a user based on known skills.
    /// V = S_user U N_k(S_user) where k=2.
    /// </summary>
    public async Task<HashSet<string>> GetValidNeighborhoodAsync(IEnumerable<string> userSkills)
    {
        var validSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var skill in userSkills)
        {
            validSkills.Add(skill);
        }

        const string query = @"
            MATCH (s:Skill)
            WHERE toLower(s.name) IN [skill IN $userSkills | toLower(skill)]
            CALL {
                WITH s
                MATCH (s)-[:REQUIRES|SUBSET_OF|BRIDGE_TO|RELATED_TO*1..2]-(neighbor:Skill)
                RETURN neighbor.name as Name
            }
            RETURN DISTINCT Name
        ";

        try 
        {
            await using var session = _driver.AsyncSession();
            var result = await session.ExecuteReadAsync(async tx => {
                var cursor = await tx.RunAsync(query, new { userSkills });
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

    public async Task<HashSet<string>> GetPrerequisiteMetSkillsAsync(IEnumerable<string> userSkills, IEnumerable<string> missingSkills)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        const string query = @"
            MATCH (parent:Skill)
            WHERE toLower(parent.name) IN [s IN $userSkills | toLower(s)]
            MATCH (child:Skill)-[:SUBSET_OF]->(parent)
            WHERE toLower(child.name) IN [s IN $missingSkills | toLower(s)]
            RETURN DISTINCT child.name AS Name
        ";

        try
        {
            await using var session = _driver.AsyncSession();
            var records = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(query, new { userSkills = userSkills.ToList(), missingSkills = missingSkills.ToList() });
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

    public async Task<HashSet<string>> GetImplicitlyDiscoveredSkillsAsync(IEnumerable<string> userSkills)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        const string query = @"
            MATCH (child:Skill)
            WHERE toLower(child.name) IN [s IN $userSkills | toLower(s)]
            // Traverse up the graph to find parents/foundations
            MATCH (child)-[:SUBSET_OF|IS_PARENT_OF*1..2]->(parent:Skill)
            RETURN DISTINCT parent.name AS Name
        ";

        try
        {
            await using var session = _driver.AsyncSession();
            var records = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(query, new { userSkills = userSkills.ToList() });
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
