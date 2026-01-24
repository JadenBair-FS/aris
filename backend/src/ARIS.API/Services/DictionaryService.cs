using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using System.Text;

namespace ARIS.API.Services;

public class DictionaryService
{
    private readonly ArisDbContext _context;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IChatClient _chatClient;
    private readonly ILogger<DictionaryService> _logger;

    public DictionaryService(ArisDbContext context, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IChatClient chatClient, ILogger<DictionaryService> logger)
    {
        _context = context;
        _embeddingGenerator = embeddingGenerator;
        _chatClient = chatClient;
        _logger = logger;
    }

    public async Task<List<RefRole>> SearchRolesAsync(string query, int limit = 5)
    {
        _logger.LogInformation("Generating embedding for query: {Query}", query);
        var embeddings = await _embeddingGenerator.GenerateAsync([query]);
        var vectorData = embeddings[0].Vector;
        
        // Log first few dimensions to see if it changes
        _logger.LogInformation("Vector Preview: [{V1}, {V2}, {V3}...]", vectorData.Span[0], vectorData.Span[1], vectorData.Span[2]);

        var vector = new Vector(vectorData);

        return await _context.Roles
            .Where(r => r.Embedding != null)
            .OrderBy(r => r.Embedding!.CosineDistance(vector))
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RefSkill>> SearchSkillsAsync(string query, int limit = 10)
    {
        _logger.LogInformation("Generating embedding for query: {Query}", query);
        var embeddings = await _embeddingGenerator.GenerateAsync([query]);
        var vectorData = embeddings[0].Vector;
        
        // Log first few dimensions
        _logger.LogInformation("Vector Preview: [{V1}, {V2}, {V3}...]", vectorData.Span[0], vectorData.Span[1], vectorData.Span[2]);

        var vector = new Vector(vectorData);

        return await _context.Skills
            .Where(s => s.Embedding != null)
            .OrderBy(s => s.Embedding!.CosineDistance(vector))
            .Take(limit)
            .ToListAsync();
    }

    public async Task<string> GetJobRecommendationsAsync(string userPrompt)
    {
        //Find relevant roles based on the user's prompt
        var relevantRoles = await SearchRolesAsync(userPrompt, limit: 5);

        //Construct Prompt
        var sb = new StringBuilder();
        sb.AppendLine("You are a helpful career counselor. Using the following database of job roles, recommend the best fits for the user.");
        sb.AppendLine("Database Matches:");
        foreach (var role in relevantRoles)
        {
            sb.AppendLine($"- {role.Title}: {role.Description}");
        }
        sb.AppendLine();
        sb.AppendLine($"User Query: {userPrompt}");
        sb.AppendLine("Response:");

        // Generate
        var response = await _chatClient.CompleteAsync(sb.ToString());
        return response.Message.Text ?? "No response generated.";
    }

    public async Task<string> GetSkillRecommendationsAsync(string userPrompt)
    {
        // Find the target role
        _logger.LogInformation("Generating embedding for skill gap analysis: {Query}", userPrompt);
        var embeddings = await _embeddingGenerator.GenerateAsync([userPrompt]);
        var vectorData = embeddings[0].Vector;
        var vector = new Vector(vectorData);

        var targetRole = await _context.Roles
            .Include(r => r.RoleSkills)
            .ThenInclude(rs => rs.Skill)
            .Where(r => r.Embedding != null)
            .OrderBy(r => r.Embedding!.CosineDistance(vector))
            .FirstOrDefaultAsync();

        if (targetRole == null)
            return "Could not identify a relevant job role from your query.";

        // Construct Prompt
        var sb = new StringBuilder();
        sb.AppendLine($"You are a mentor. The user wants to be a '{targetRole.Title}'.");
        sb.AppendLine("Here are the standard skills required for this role from our database:");

        var roleSkills = targetRole.RoleSkills.OrderByDescending(x => x.Importance).Take(15);

        // Take top 15 skills
        foreach (var rs in roleSkills)
        {
            sb.AppendLine($"- {rs.Skill.Name}");
        }
        sb.AppendLine();
        sb.AppendLine($"User Query: {userPrompt}");
        sb.AppendLine("Analyze the user's request and provide a skill gap analysis and recommendations.");
        sb.AppendLine("Response:");

        var fullString = sb.ToString();

        // Generate
        var response = await _chatClient.CompleteAsync(fullString);
        return response.Message.Text ?? "No response generated.";
    }
}