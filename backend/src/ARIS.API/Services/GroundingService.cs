using ARIS.Shared.Data;
using ARIS.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace ARIS.API.Services;

public class GroundingService
{
    private readonly GraphService _graphService;
    private readonly ArisDbContext _context;
    private readonly ILogger<GroundingService> _logger;

    public GroundingService(GraphService graphService, ArisDbContext context, ILogger<GroundingService> logger)
    {
        _graphService = graphService;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Calculates the Graph Grounding Score for generated text (thesis RQ2 novel metric).
    /// Score = Verified Skills / Total Skills Extracted from text.
    /// A score of 1.0 means every skill entity mentioned in the text is in the user's valid neighborhood.
    /// </summary>
    public async Task<GroundingResult> CalculateGroundingScoreAsync(string generatedText, IEnumerable<string> userExplicitSkills)
    {
        var validNeighborhood = await _graphService.GetValidNeighborhoodAsync(userExplicitSkills);
        var allSkillNames = await _context.Skills.Select(s => s.Name).ToListAsync();

        var extractedSkills = ExtractSkillsFromText(generatedText, allSkillNames);

        if (extractedSkills.Count == 0)
        {
            return new GroundingResult { Score = 1.0, Hallucinations = [] };
        }

        var verifiedCount = 0;
        var hallucinations = new List<string>();

        foreach (var skill in extractedSkills)
        {
            if (validNeighborhood.Contains(skill))
            {
                verifiedCount++;
            }
            else
            {
                hallucinations.Add(skill);
            }
        }

        double score = (double)verifiedCount / extractedSkills.Count;

        return new GroundingResult
        {
            Score = score,
            Hallucinations = hallucinations,
            TotalEntitiesFound = extractedSkills.Count,
            ValidNeighborhoodSize = validNeighborhood.Count
        };
    }

    /// <summary>
    /// Calculates grounding score from a pre-extracted list of canonical skill names.
    /// Use for Pipeline C, where all identified skill names are already canonical —
    /// bypasses ExtractSkillsFromText to avoid spurious sub-skill substring matches.
    /// Score = |identified ∩ validNeighborhood| / |identified|.
    /// </summary>
    public async Task<GroundingResult> CalculateGroundingScoreFromSkillsAsync(
        IEnumerable<string> identifiedSkills,
        IEnumerable<string> userExplicitSkills)
    {
        var skillList = identifiedSkills
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (skillList.Count == 0)
            return new GroundingResult { Score = 1.0, Hallucinations = [] };

        var validNeighborhood = await _graphService.GetValidNeighborhoodAsync(userExplicitSkills);
        var hallucinations = new List<string>();
        int verifiedCount = 0;

        foreach (var skill in skillList)
        {
            if (validNeighborhood.Contains(skill))
                verifiedCount++;
            else
                hallucinations.Add(skill);
        }

        return new GroundingResult
        {
            Score = (double)verifiedCount / skillList.Count,
            Hallucinations = hallucinations,
            TotalEntitiesFound = skillList.Count,
            ValidNeighborhoodSize = validNeighborhood.Count
        };
    }

    /// <summary>
    /// Extracts canonical skill names from text using case-insensitive substring matching
    /// against the reference dictionary. Returns matched canonical skill names (not raw text spans).
    /// </summary>
    private static List<string> ExtractSkillsFromText(string text, List<string> allSkillNames)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var lowerText = text.ToLowerInvariant();
        var found = new List<string>();

        foreach (var skillName in allSkillNames)
        {
            if (string.IsNullOrWhiteSpace(skillName)) continue;

            var lowerSkill = skillName.ToLowerInvariant();
            var idx = lowerText.IndexOf(lowerSkill, StringComparison.Ordinal);
            if (idx < 0) continue;

            var charBefore = idx > 0 ? lowerText[idx - 1] : ' ';
            var charAfter = idx + lowerSkill.Length < lowerText.Length ? lowerText[idx + lowerSkill.Length] : ' ';

            bool startBoundary = !char.IsLetterOrDigit(charBefore);
            bool endBoundary = !char.IsLetterOrDigit(charAfter);

            if (startBoundary && endBoundary)
                found.Add(skillName);
        }

        return found;
    }
}
