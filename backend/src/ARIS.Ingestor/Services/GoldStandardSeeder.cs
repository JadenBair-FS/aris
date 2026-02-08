using System.Text.Json;
using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pgvector;
using System.Text;

namespace ARIS.Ingestor.Services;

public class GoldStandardSeeder
{
    private readonly ArisDbContext _context;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ILogger<GoldStandardSeeder> _logger;
    private readonly Neo4jIngestionService _neo4jService;

    public GoldStandardSeeder(ArisDbContext context, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, ILogger<GoldStandardSeeder> logger, Neo4jIngestionService neo4jService)
    {
        _context = context;
        _embeddingGenerator = embeddingGenerator;
        _logger = logger;
        _neo4jService = neo4jService;
    }

    public async Task RunSeedingAsync(string datasetPath)
    {
        _logger.LogInformation("Starting Gold Standard Seeding from: {Path}", datasetPath);

        var idMap = new Dictionary<string, Guid>();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Process Jobs
        var jobsPath = Path.Combine(datasetPath, "jobs.json");
        if (File.Exists(jobsPath))
        {
            var jobsData = JsonSerializer.Deserialize<List<GoldStandardJobDto>>(File.ReadAllText(jobsPath), options);
            if (jobsData != null)
            {
                foreach (var jobDto in jobsData)
                {
                    _logger.LogInformation("Processing Job: {Id}", jobDto.Id);

                    // Generate Embedding
                    var symmetricString = BuildJobSymmetricString(jobDto.CleanSignal);
                    var embeddings = await _embeddingGenerator.GenerateAsync([symmetricString]);
                    var vectorData = embeddings[0].Vector;

                    var job = new JobPosting
                    {
                        RecruiterId = "gold_standard",
                        RawDescription = jobDto.Description,
                        CleanSignal = jobDto.CleanSignal,
                        Embedding = new Vector(vectorData),
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    _context.JobPostings.Add(job);
                    await _context.SaveChangesAsync();

                    idMap[jobDto.Id] = job.Id;
                }
            }
        }

        // Process Users
        var usersPath = Path.Combine(datasetPath, "users.json");
        if (File.Exists(usersPath))
        {
            var usersData = JsonSerializer.Deserialize<List<GoldStandardUserDto>>(File.ReadAllText(usersPath), options);
            if (usersData != null)
            {
                foreach (var userDto in usersData)
                {
                    _logger.LogInformation("Processing User: {Id}", userDto.Id);

                    // Generate Embedding
                    var symmetricString = BuildResumeSymmetricString(userDto.CleanSignal);
                    var embeddings = await _embeddingGenerator.GenerateAsync([symmetricString]);
                    var vectorData = embeddings[0].Vector;

                    var user = new UserProfile
                    {
                        UserId = userDto.Id, // Use the test ID as the UserID
                        RawResume = JsonSerializer.Serialize(new { content = userDto.RawText }),
                        CleanSignal = userDto.CleanSignal,
                        Embedding = new Vector(vectorData),
                        UpdatedAt = DateTime.UtcNow
                    };

                    _context.UserProfiles.Add(user);
                    await _context.SaveChangesAsync();

                    idMap[userDto.Id] = user.Id;
                }
            }
        }

        // Save Map
        var mapPath = Path.Combine(datasetPath, "id_map.json");
        await File.WriteAllTextAsync(mapPath, JsonSerializer.Serialize(idMap, new JsonSerializerOptions { WriteIndented = true }));
        _logger.LogInformation("Seeding Complete. ID Map saved to {MapPath}", mapPath);
    }

    private static string BuildJobSymmetricString(JobPostingCleanSignal signal)
    {
        var sb = new StringBuilder();
        foreach (var role in signal.TargetRoles) sb.Append(role.Title).Append(' ');
        foreach (var skill in signal.RequiredSkills) sb.Append(skill.Name).Append(' ');
        return sb.ToString().Trim();
    }

    private static string BuildResumeSymmetricString(ResumeCleanSignal signal)
    {
        var sb = new StringBuilder();
        foreach (var role in signal.Roles) sb.Append(role.Title).Append(' ');
        foreach (var skill in signal.Skills) sb.Append(skill.Name).Append(' ');
        return sb.ToString().Trim();
    }

    // DTOs for the JSON file structure
    private class GoldStandardJobDto
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public JobPostingCleanSignal CleanSignal { get; set; } = new();
    }

    private class GoldStandardUserDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string RawText { get; set; } = "";
        public ResumeCleanSignal CleanSignal { get; set; } = new();
    }
}
