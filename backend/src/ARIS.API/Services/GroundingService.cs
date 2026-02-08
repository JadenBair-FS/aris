using System.Text.RegularExpressions;
using ARIS.Shared.Models;

namespace ARIS.API.Services;

public class GroundingService
{
    private readonly GraphService _graphService;
    private readonly ILogger<GroundingService> _logger;

    public GroundingService(GraphService graphService, ILogger<GroundingService> logger)
    {
        _graphService = graphService;
        _logger = logger;
    }

    /// <summary>
    /// Calculates the Graph Grounding Score for generated text.
    /// Score = Verified Skills / Total Skills Generated
    /// </summary>
    public async Task<GroundingResult> CalculateGroundingScoreAsync(string generatedText, IEnumerable<string> userExplicitSkills)
    {
        var validNeighborhood = await _graphService.GetValidNeighborhoodAsync(userExplicitSkills);

        var extractedSkills = ExtractSkillsFromText(generatedText);

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

    private List<string> ExtractSkillsFromText(string text)
    {
        return new List<string>(); 
    }
}
