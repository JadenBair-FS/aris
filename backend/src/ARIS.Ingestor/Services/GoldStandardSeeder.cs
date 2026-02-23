using System.Text.Json;
using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
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

                    // RE-GROUND with new thresholds and domain awareness
                    await GroundJobSignalAsync(jobDto.CleanSignal);

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

                    // RE-GROUND with new thresholds and domain awareness
                    await GroundResumeSignalAsync(userDto.CleanSignal);

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

    private async Task GroundJobSignalAsync(JobPostingCleanSignal signal)
    {
        var roleTitles = signal.TargetRoles.Select(r => r.Title).ToList();
        var skillNames = signal.RequiredSkills.Select(s => s.Name).ToList();

        if (roleTitles.Count == 0 && skillNames.Count == 0) return;

        var allTexts = roleTitles.Concat(skillNames).ToList();
        var embeddings = await _embeddingGenerator.GenerateAsync(allTexts);
        var vectors = embeddings.Select(e => new Vector(e.Vector)).ToList();

        for (int i = 0; i < roleTitles.Count; i++)
        {
            var vector = vectors[i];
            var match = await _context.Roles
                .Where(r => r.Embedding != null)
                .Select(r => new { r.Title, r.OnetCode, Distance = r.Embedding!.CosineDistance(vector) })
                .OrderBy(x => x.Distance)
                .FirstOrDefaultAsync();

            if (match != null && match.Distance < 0.35)
            {
                signal.TargetRoles[i].Title = match.Title;
                signal.TargetRoles[i].OnetCode = match.OnetCode;
            }
        }

        var primaryRole = signal.TargetRoles.FirstOrDefault(r => r.Priority == "Primary") ?? signal.TargetRoles.FirstOrDefault();
        string? domainPrefix = null;
        if (primaryRole?.OnetCode != null && primaryRole.OnetCode.Contains('-'))
        {
            domainPrefix = primaryRole.OnetCode.Split('-')[0];
        }

        int skillOffset = roleTitles.Count;
        for (int i = 0; i < skillNames.Count; i++)
        {
            var vector = vectors[skillOffset + i];
            var domainMatch = await _context.RoleSkills
                .Include(rs => rs.Skill)
                .Include(rs => rs.Role)
                .Where(rs => rs.Role.OnetCode != null && rs.Role.OnetCode.StartsWith(domainPrefix ?? "NONE"))
                .Where(rs => rs.Skill.Embedding != null)
                .Select(rs => new { rs.Skill.Name, Distance = rs.Skill.Embedding!.CosineDistance(vector) })
                .OrderBy(x => x.Distance)
                .FirstOrDefaultAsync();

            if (domainMatch != null && domainMatch.Distance < 0.25)
            {
                signal.RequiredSkills[i].Name = domainMatch.Name;
            }
            else
            {
                var generalMatch = await _context.Skills
                    .Where(s => s.Embedding != null)
                    .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(vector) })
                    .OrderBy(x => x.Distance)
                    .FirstOrDefaultAsync();

                if (generalMatch != null && generalMatch.Distance < 0.35)
                {
                    signal.RequiredSkills[i].Name = generalMatch.Name;
                }
            }
        }
    }

    private async Task GroundResumeSignalAsync(ResumeCleanSignal signal)
    {
        var roleTitles = signal.Roles.Select(r => r.Title).ToList();
        var skillNames = signal.Skills.Select(s => s.Name).ToList();

        if (roleTitles.Count == 0 && skillNames.Count == 0) return;

        var allTexts = roleTitles.Concat(skillNames).ToList();
        var embeddings = await _embeddingGenerator.GenerateAsync(allTexts);
        var vectors = embeddings.Select(e => new Vector(e.Vector)).ToList();

        for (int i = 0; i < roleTitles.Count; i++)
        {
            var vector = vectors[i];
            var match = await _context.Roles
                .Where(r => r.Embedding != null)
                .Select(r => new { r.Title, r.OnetCode, Distance = r.Embedding!.CosineDistance(vector) })
                .OrderBy(x => x.Distance)
                .FirstOrDefaultAsync();

            if (match != null && match.Distance < 0.35)
            {
                signal.Roles[i].Title = match.Title;
                signal.Roles[i].OnetCode = match.OnetCode;
            }
        }

        var primaryRole = signal.Roles.FirstOrDefault(r => r.IsCurrent) ?? signal.Roles.FirstOrDefault();
        string? domainPrefix = null;
        if (primaryRole?.OnetCode != null && primaryRole.OnetCode.Contains('-'))
        {
            domainPrefix = primaryRole.OnetCode.Split('-')[0];
        }

        int skillOffset = roleTitles.Count;
        for (int i = 0; i < skillNames.Count; i++)
        {
            var vector = vectors[skillOffset + i];
            var domainMatch = await _context.RoleSkills
                .Include(rs => rs.Skill)
                .Include(rs => rs.Role)
                .Where(rs => rs.Role.OnetCode != null && rs.Role.OnetCode.StartsWith(domainPrefix ?? "NONE"))
                .Where(rs => rs.Skill.Embedding != null)
                .Select(rs => new { rs.Skill.Name, Distance = rs.Skill.Embedding!.CosineDistance(vector) })
                .OrderBy(x => x.Distance)
                .FirstOrDefaultAsync();

            if (domainMatch != null && domainMatch.Distance < 0.25)
            {
                signal.Skills[i].Name = domainMatch.Name;
            }
            else
            {
                var generalMatch = await _context.Skills
                    .Where(s => s.Embedding != null)
                    .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(vector) })
                    .OrderBy(x => x.Distance)
                    .FirstOrDefaultAsync();

                if (generalMatch != null && generalMatch.Distance < 0.35)
                {
                    signal.Skills[i].Name = generalMatch.Name;
                }
            }
        }
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
