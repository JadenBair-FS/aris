using ARIS.Ingestor.Data;
using ARIS.Ingestor.Services;
using ARIS.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Pgvector;

namespace ARIS.Ingestor;

public class IngestionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly OnetService _onetService;
    private readonly ILogger<IngestionWorker> _logger;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;

    public IngestionWorker(
        IServiceProvider serviceProvider,
        OnetService onetService,
        ILogger<IngestionWorker> logger,
        IHostApplicationLifetime hostApplicationLifetime,
        IEmbeddingGenerator<string, Embedding<float>> embeddingService)
    {
        _serviceProvider = serviceProvider;
        _onetService = onetService;
        _logger = logger;
        _hostApplicationLifetime = hostApplicationLifetime;
        _embeddingService = embeddingService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Ingestion Worker Started.");

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ArisDbContext>();

        // Ensure Database Created & Migrated
        _logger.LogInformation("Ensuring database is created...");
        await dbContext.Database.EnsureCreatedAsync(stoppingToken);

        // Fetch Occupations
        _logger.LogInformation("Fetching Occupations from O*NET...");
        var occupations = await _onetService.GetAllOccupationsAsync(stoppingToken);
        _logger.LogInformation("Found {Count} occupations.", occupations.Count);

        // 3. Process a subset for testing (first 5)
        var subset = occupations.Take(5).ToList();

        foreach (var occDto in subset)
        {
            if (stoppingToken.IsCancellationRequested) break;

            _logger.LogInformation("Processing: {Title} ({Code})", occDto.Title, occDto.Code);

            // Check if exists
            var existingRole = await dbContext.Roles
                .FirstOrDefaultAsync(r => r.OnetCode == occDto.Code, stoppingToken);

            if (existingRole != null)
            {
                _logger.LogInformation("Role already exists. Skipping.");
                continue;
            }

            // Get Details
            var details = await _onetService.GetOccupationDetailsAsync(occDto.Code, stoppingToken);
            if (details == null) continue;

            // Generate Embedding for Role (Title + Description)
            var roleText = $"{details.Title}: {details.Description}";
            var roleEmbedding = await GenerateEmbeddingAsync(roleText);

            // Create Role Entity
            var role = new RefRole
            {
                Title = details.Title,
                OnetCode = details.Code,
                Description = details.Description,
                Embedding = roleEmbedding
            };

            dbContext.Roles.Add(role);
            await dbContext.SaveChangesAsync(stoppingToken); // Save to get RoleId

            // Process Tasks
            if (details.Tasks != null)
            {
                foreach (var taskName in details.Tasks)
                {
                    await ProcessSkillAsync(dbContext, role.Id, taskName, "ONET_Task", stoppingToken);
                }
            }

            // Process Skills
            if (details.Skills != null)
            {
                foreach (var skillName in details.Skills)
                {
                    await ProcessSkillAsync(dbContext, role.Id, skillName, "ONET_Skill", stoppingToken);
                }
            }

            await dbContext.SaveChangesAsync(stoppingToken);
        }

        _logger.LogInformation("Ingestion Complete.");
        _hostApplicationLifetime.StopApplication();
    }

    private async Task ProcessSkillAsync(ArisDbContext dbContext, int roleId, string skillName, string source, CancellationToken ct)
    {
        // Check if Skill exists (Exact Match for now)
        // Note: In a real scenario, we might want fuzzy matching or vector search here to avoid duplicates like "Programming" vs "Computer Programming"
        var skill = await dbContext.Skills.FirstOrDefaultAsync(s => s.Name == skillName, ct);

        if (skill == null)
        {
            // Generate Embedding for Skill
            var skillEmbedding = await GenerateEmbeddingAsync(skillName);

            skill = new RefSkill
            {
                Name = skillName,
                Source = source,
                Embedding = skillEmbedding
            };
            dbContext.Skills.Add(skill);
            await dbContext.SaveChangesAsync(ct); // Save to get SkillId
        }

        // Link to Role
        var roleSkill = await dbContext.RoleSkills
            .FirstOrDefaultAsync(rs => rs.RoleId == roleId && rs.SkillId == skill.Id, ct);

        if (roleSkill == null)
        {
            roleSkill = new RefRoleSkill
            {
                RoleId = roleId,
                SkillId = skill.Id,
                // Defaulting importance/level for now as we are just grabbing names
                Importance = 0,
                Level = 0
            };
            dbContext.RoleSkills.Add(roleSkill);
        }
    }

    private async Task<Vector?> GenerateEmbeddingAsync(string text)
    {
        try
        {
            // Use the correct method from IEmbeddingGenerator interface
            var embedding = await _embeddingService.GenerateVectorAsync(text);
            if (embedding.IsEmpty) return null;
            // Convert Embedding<float> to Pgvector.Vector
            return new Vector(embedding);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate embedding for text: {Text}", text.Length > 50 ? text.Substring(0, 50) + "..." : text);
            return null;
        }
    }
}

