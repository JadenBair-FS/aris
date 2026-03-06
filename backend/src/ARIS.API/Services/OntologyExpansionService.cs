using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using System.Text.Json;

namespace ARIS.API.Services;

/// <summary>
/// Implements distributed self-consistency ontology expansion (Wang et al. 2023).
/// Skills extracted by the LLM that have no acceptable canonical match are staged here.
/// Once a skill is independently observed from 3 separate source documents it is promoted
/// to ref_skills and Neo4j, and an LLM infers its SUBSET_OF / BRIDGE_TO relationships.
/// </summary>
public class OntologyExpansionService
{
    private const int PromotionThreshold = 3;
    private const string PromotedSource = "Extracted";

    private readonly ArisDbContext _context;
    private readonly GraphService _graphService;
    private readonly IChatClient _chatClient;
    private readonly ILogger<OntologyExpansionService> _logger;

    public OntologyExpansionService(
        ArisDbContext context,
        GraphService graphService,
        IChatClient chatClient,
        ILogger<OntologyExpansionService> logger)
    {
        _context = context;
        _graphService = graphService;
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// Records a skill that had no acceptable canonical match during grounding.
    /// Upserts into candidate_skills and promotes automatically when the observation
    /// count reaches the promotion threshold from unique source documents.
    /// </summary>
    public async Task RecordCandidateAsync(
        string name,
        Vector embedding,
        string? domainPrefix,
        bool isTech,
        string sourceDocId)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var normalized = name.Trim().ToLowerInvariant();

        try
        {
            var existing = await _context.CandidateSkills.FirstOrDefaultAsync(c =>
                c.NormalizedName == normalized &&
                (domainPrefix == null ? c.DomainPrefix == null : c.DomainPrefix == domainPrefix));

            if (existing == null)
            {
                _context.CandidateSkills.Add(new CandidateSkill
                {
                    Name = name.Trim(),
                    NormalizedName = normalized,
                    DomainPrefix = domainPrefix,
                    IsTech = isTech,
                    Embedding = embedding,
                    ObservationCount = 1,
                    SourceDocumentIds = [sourceDocId],
                    Status = "candidate"
                });
                await _context.SaveChangesAsync();
                _logger.LogInformation("Candidate skill '{Skill}' recorded (domain: {Domain}).", name, domainPrefix ?? "cross-domain");
                return;
            }

            if (existing.Status != "candidate") return;
            if (existing.SourceDocumentIds.Contains(sourceDocId)) return;

            existing.SourceDocumentIds = [.. existing.SourceDocumentIds, sourceDocId];
            existing.ObservationCount++;
            existing.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Candidate skill '{Skill}' now has {Count} observations.", name, existing.ObservationCount);

            if (existing.ObservationCount >= PromotionThreshold)
                await PromoteSkillAsync(existing);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record candidate skill '{Skill}'.", name);
        }
    }

    /// <summary>
    /// Promotes a validated candidate skill to ref_skills and Neo4j,
    /// then infers its structural relationships to existing skills.
    /// </summary>
    private async Task PromoteSkillAsync(CandidateSkill candidate)
    {
        _logger.LogInformation("Promoting candidate skill '{Skill}' after {Count} observations.",
            candidate.Name, candidate.ObservationCount);

        try
        {
            var alreadyExists = await _context.Skills
                .AnyAsync(s => s.Name.ToLower() == candidate.NormalizedName);

            if (alreadyExists)
            {
                candidate.Status = "promoted";
                await _context.SaveChangesAsync();
                return;
            }

            var newSkill = new RefSkill
            {
                Name = candidate.Name,
                Source = PromotedSource,
                IsTech = candidate.IsTech,
                Embedding = candidate.Embedding
            };
            _context.Skills.Add(newSkill);
            await _context.SaveChangesAsync();

            await _graphService.CreateSkillNodeAsync(candidate.Name, PromotedSource, candidate.IsTech);
            await InferRelationshipsAsync(candidate);

            candidate.Status = "promoted";
            candidate.PromotedSkillId = newSkill.Id;
            candidate.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Skill '{Skill}' promoted to canonical graph (ref_skills id={Id}).",
                candidate.Name, newSkill.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to promote candidate skill '{Skill}'.", candidate.Name);
        }
    }

    /// <summary>
    /// Finds the top-15 most similar canonical skills in the candidate's domain,
    /// asks the LLM to classify SUBSET_OF / BRIDGE_TO relationships, and writes
    /// the resulting edges to Neo4j.
    /// </summary>
    private async Task InferRelationshipsAsync(CandidateSkill candidate)
    {
        if (candidate.Embedding == null) return;

        try
        {
            var vector = candidate.Embedding;
            List<string> neighborNames;

            if (candidate.DomainPrefix != null)
            {
                var domainNeighbors = await _context.RoleSkills
                    .Include(rs => rs.Skill)
                    .Include(rs => rs.Role)
                    .Where(rs => rs.Role.OnetCode != null
                              && rs.Role.OnetCode.StartsWith(candidate.DomainPrefix)
                              && rs.Skill.Embedding != null
                              && rs.Skill.Name != candidate.Name)
                    .Select(rs => new { rs.Skill.Name, Distance = rs.Skill.Embedding!.CosineDistance(vector) })
                    .OrderBy(x => x.Distance)
                    .Take(15)
                    .ToListAsync();

                neighborNames = domainNeighbors.Count > 0
                    ? domainNeighbors.Select(n => n.Name).ToList()
                    : await _context.Skills
                        .Where(s => s.Embedding != null && s.Name != candidate.Name)
                        .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(vector) })
                        .OrderBy(x => x.Distance)
                        .Take(15)
                        .Select(x => x.Name)
                        .ToListAsync();
            }
            else
            {
                neighborNames = await _context.Skills
                    .Where(s => s.Embedding != null && s.Name != candidate.Name)
                    .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(vector) })
                    .OrderBy(x => x.Distance)
                    .Take(15)
                    .Select(x => x.Name)
                    .ToListAsync();
            }

            var neighborList = string.Join("\n", neighborNames.Select((n, i) => $"{i + 1}. {n}"));
            await RunRelationshipInferenceAsync(candidate, neighborList);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to infer relationships for skill '{Skill}'.", candidate.Name);
        }
    }

    private async Task RunRelationshipInferenceAsync(CandidateSkill candidate, string neighborList)
    {
        try
        {
            var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "SkillRelationshipInference.md");
            var template = await File.ReadAllTextAsync(promptPath);

            var domainName = candidate.DomainPrefix != null
                ? await _context.Roles
                    .Where(r => r.OnetCode != null && r.OnetCode.StartsWith(candidate.DomainPrefix))
                    .Select(r => r.Title)
                    .FirstOrDefaultAsync() ?? candidate.DomainPrefix
                : "cross-domain";

            var prompt = template
                .Replace("{skill_name}", candidate.Name)
                .Replace("{domain_name}", domainName)
                .Replace("{domain_prefix}", candidate.DomainPrefix ?? "N/A")
                .Replace("{candidate_neighbors}", neighborList);

            var chatOptions = new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.Json,
                Temperature = 0.1f,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["stream"] = false,
                    ["num_ctx"] = 2048,
                }
            };

            var response = await _chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.System, "You are a skill ontology expert. Return only valid JSON matching the requested schema."),
                 new ChatMessage(ChatRole.User, prompt)],
                chatOptions);

            var json = response?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(json)) return;

            var startIdx = json.IndexOf('{');
            var endIdx = json.LastIndexOf('}');
            if (startIdx >= 0 && endIdx > startIdx)
                json = json[startIdx..(endIdx + 1)];

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var subsetOf = root.TryGetProperty("subset_of", out var subProp)
                ? subProp.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList()
                : new List<string>();

            var bridgeTo = root.TryGetProperty("bridge_to", out var bridgeProp)
                ? bridgeProp.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList()
                : new List<string>();

            if (subsetOf.Count > 0 || bridgeTo.Count > 0)
            {
                await _graphService.CreateSkillEdgesAsync(candidate.Name, subsetOf, bridgeTo);
                _logger.LogInformation("Skill '{Skill}': SUBSET_OF=[{Sub}] BRIDGE_TO=[{Bridge}].",
                    candidate.Name,
                    string.Join(", ", subsetOf),
                    string.Join(", ", bridgeTo));
            }
            else
            {
                _logger.LogInformation("Skill '{Skill}': no relationships inferred — standalone node.", candidate.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM relationship inference failed for skill '{Skill}'.", candidate.Name);
        }
    }
}
