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

            // Require word-boundary-like matching to avoid "SQL" matching "Visual"
            // by checking the character before and after the match position.
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
