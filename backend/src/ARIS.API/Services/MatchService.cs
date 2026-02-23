using ARIS.Shared.Data;
using ARIS.Shared.Helpers;
using ARIS.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;

namespace ARIS.API.Services;

public class MatchService
{
    private readonly ArisDbContext _context;
    private readonly GraphService _graphService;
    private readonly GroundingService _groundingService;
    private readonly IChatClient _chatClient;
    private readonly ILogger<MatchService> _logger;

    public MatchService(ArisDbContext context, GraphService graphService, GroundingService groundingService, IChatClient chatClient, ILogger<MatchService> logger)
    {
        _context = context;
        _graphService = graphService;
        _groundingService = groundingService;
        _chatClient = chatClient;
        _logger = logger;
    }

    public async Task<MatchAnalysisResult?> AnalyzeMatchAsync(Guid userProfileId, Guid jobId)
    {
        var user = await _context.UserProfiles.FindAsync(userProfileId);
        var job = await _context.JobPostings.FindAsync(jobId);

        if (user == null)
        {
            _logger.LogWarning("Match analysis failed: User {UserId} not found.", userProfileId);
            return null;
        }
        if (job == null)
        {
             _logger.LogWarning("Match analysis failed: Job {JobId} not found.", jobId);
             return null;
        }
        if (user.CleanSignal == null)
        {
            _logger.LogWarning("Match analysis failed: User {UserId} has no CleanSignal.", userProfileId);
            return null;
        }
        if (job.CleanSignal == null)
        {
            _logger.LogWarning("Match analysis failed: Job {JobId} has no CleanSignal.", jobId);
            return null;
        }
        if (user.Embedding == null)
        {
             _logger.LogWarning("Match analysis failed: User {UserId} has no Embedding.", userProfileId);
             return null;
        }
        if (job.Embedding == null)
        {
            _logger.LogWarning("Match analysis failed: Job {JobId} has no Embedding.", jobId);
            return null;
        }

        var distance = await _context.JobPostings
            .Where(j => j.Id == jobId)
            .Select(j => j.Embedding!.CosineDistance(user.Embedding))
            .FirstOrDefaultAsync();

        var similarity = 1 - distance;

        var userSkills = user.CleanSignal.Skills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var jobSkills = job.CleanSignal.RequiredSkills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var primaryRole = job.CleanSignal?.TargetRoles?.FirstOrDefault();
        bool isTech = DomainClassifier.IsTechDomain(primaryRole?.OnetCode, primaryRole?.Title);

        // Gate the tech-skill post-filter on the candidate's domain, not the job's domain.
        var candidatePrimaryRole = user.CleanSignal?.Roles?.FirstOrDefault(r => r.IsCurrent)
                                   ?? user.CleanSignal?.Roles?.FirstOrDefault();
        bool isCandidateTech = DomainClassifier.IsTechDomain(
            candidatePrimaryRole?.OnetCode, candidatePrimaryRole?.Title);

        var implicitSkills = await _graphService.GetImplicitlyDiscoveredSkillsAsync(userSkills, isTech);

        var totalUserSkills = new HashSet<string>(userSkills, StringComparer.OrdinalIgnoreCase);
        foreach (var s in implicitSkills) totalUserSkills.Add(s);

        var matchingSkills = jobSkills.Intersect(userSkills, StringComparer.OrdinalIgnoreCase).ToList();

        var implicitlyMatched = jobSkills
            .Except(matchingSkills, StringComparer.OrdinalIgnoreCase)
            .Intersect(implicitSkills, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation("Job Skills: {JobSkills}", string.Join(", ", jobSkills));
        _logger.LogInformation("Matching Skills found: {Matches}", string.Join(", ", matchingSkills));
        _logger.LogInformation("Implicitly Matched found: {ImplicitMatches}", string.Join(", ", implicitlyMatched));
        _logger.LogInformation("Implicit Skills from Graph: {ImplicitCount}", implicitSkills.Count);

        var missingSkills = jobSkills
            .Except(matchingSkills, StringComparer.OrdinalIgnoreCase)
            .Except(implicitlyMatched, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Universal O*NET competencies are generic human capabilities — never a gap.
        missingSkills = missingSkills
            .Where(s => !GraphService.UniversalSkills.Contains(s))
            .ToList();

        var bridgeable = new List<SkillGapItem>();
        var prerequisiteMet = new List<SkillGapItem>();
        var hardGaps = new List<SkillGapItem>();

        if (missingSkills.Count > 0)
        {
            var neighborhood = await _graphService.GetValidNeighborhoodAsync(totalUserSkills, isTech);
            var prerequisiteMetSet = await _graphService.GetPrerequisiteMetSkillsAsync(totalUserSkills, missingSkills, isTech);

            // Only block Roadmap.sh-sourced tech skills for non-tech candidates.
            // This allows domain tool bridges (CRM, EHR, Salesforce, etc.) to show as
            // bridgeable for non-tech candidates while still preventing pure engineering
            // skills (TypeScript, Kubernetes, etc.) from appearing as valid bridges.
            var roadmapTechSkillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!isCandidateTech)
            {
                var roadmapTechList = await _context.Skills
                    .Where(s => s.IsTech && s.Source == "Roadmap.sh")
                    .Select(s => s.Name)
                    .ToListAsync();
                roadmapTechSkillNames = new HashSet<string>(roadmapTechList, StringComparer.OrdinalIgnoreCase);

                neighborhood = neighborhood
                    .Except(roadmapTechSkillNames, StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                prerequisiteMetSet = prerequisiteMetSet
                    .Except(roadmapTechSkillNames, StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            _logger.LogInformation("Graph Neighborhood for User: {Neighborhood}", string.Join(", ", neighborhood));
            _logger.LogInformation("Prerequisite-Met Skills: {PrereqMet}", string.Join(", ", prerequisiteMetSet));

            // Fetch bridge path data for all missing skills to enrich SkillGapItem objects.
            var bridgePaths = await _graphService.GetBridgeablePathsAsync(totalUserSkills, missingSkills);
            var bridgePathBySkill = bridgePaths.ToDictionary(
                p => p.SkillName,
                p => (p.ViaSkill, p.BridgeType, p.BridgeSource),
                StringComparer.OrdinalIgnoreCase);

            foreach (var skill in missingSkills)
            {
                // For non-tech candidates, Roadmap.sh tech skills are always hard gaps.
                if (!isCandidateTech && roadmapTechSkillNames.Contains(skill))
                {
                    var jobSkillData = GetJobSkillData(job.CleanSignal!, skill);
                    hardGaps.Add(new SkillGapItem
                    {
                        SkillName = skill,
                        Importance = jobSkillData.importance,
                        YearsRequired = jobSkillData.years
                    });
                    continue;
                }

                var (importance, years) = GetJobSkillData(job.CleanSignal!, skill);

                // Preferred skills are aspirational — never a hard gap.
                if (importance.Equals("Preferred", StringComparison.OrdinalIgnoreCase))
                {
                    bridgeable.Add(BuildSkillGapItem(skill, importance, years, bridgePathBySkill));
                    continue;
                }

                // Rules-based Hard Gap detection for Certifications.
                bool isCertification = skill.Contains("Certified", StringComparison.OrdinalIgnoreCase) ||
                                     skill.Contains("CST", StringComparison.OrdinalIgnoreCase) ||
                                     skill.Contains("License", StringComparison.OrdinalIgnoreCase) ||
                                     skill.Contains("Certification", StringComparison.OrdinalIgnoreCase) ||
                                     skill.Contains("Credential", StringComparison.OrdinalIgnoreCase);

                if (isCertification)
                {
                    hardGaps.Add(new SkillGapItem { SkillName = skill, Importance = importance, YearsRequired = years });
                }
                else if (prerequisiteMetSet.Contains(skill))
                {
                    prerequisiteMet.Add(BuildSkillGapItem(skill, importance, years, bridgePathBySkill));
                }
                else if (neighborhood.Contains(skill))
                {
                    bridgeable.Add(BuildSkillGapItem(skill, importance, years, bridgePathBySkill));
                }
                else
                {
                    hardGaps.Add(new SkillGapItem { SkillName = skill, Importance = importance, YearsRequired = years });
                }
            }
        }

        // Compute ArisScore: blended graph+vector ranking signal (thesis RQ1/RQ2 contribution).
        // Graph coverage weights: direct match=1.0, implicit=0.8, prereqMet=0.6, bridgeable=0.4.
        // Importance weights: Essential=1.0, Preferred=0.6 (applied to both numerator and denominator).
        // When a job has no Essential/Preferred distinction, all skills default to Essential (weight=1.0)
        // and the formula is equivalent to the flat count version.
        // Capped at 1.0 to prevent over-inflation when coverage exceeds total job skills.
        // Blend: 55% vector similarity + 45% graph coverage.
        // The 45% graph weight is calibrated to correct embedding-space ranking failures
        // Group by name (case-insensitive) and take the most conservative weight when the LLM
        // emits duplicate skill entries. Essential (1.0) wins over Preferred (0.6) on conflict.
        var importanceWeights = job.CleanSignal!.RequiredSkills
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Any(s => !s.Importance.Equals("Preferred", StringComparison.OrdinalIgnoreCase)) ? 1.0 : 0.6,
                StringComparer.OrdinalIgnoreCase);

        double GetImportanceWeight(string skillName) =>
            importanceWeights.TryGetValue(skillName, out var w) ? w : 1.0;

        var weightedJobTotal = Math.Max(importanceWeights.Values.Sum(), 1.0);
        var graphCoverageScore = Math.Min(
            (matchingSkills.Sum(s => GetImportanceWeight(s) * 1.0) +
             implicitlyMatched.Sum(s => GetImportanceWeight(s) * 0.8) +
             prerequisiteMet.Sum(s => GetImportanceWeight(s.SkillName) * 0.6) +
             bridgeable.Sum(s => GetImportanceWeight(s.SkillName) * 0.4)) / weightedJobTotal,
            1.0);
        var arisScore = 0.55 * similarity + 0.45 * graphCoverageScore;

        return new MatchAnalysisResult
        {
            JobId = job.Id,
            VectorSimilarity = similarity,
            ArisScore = arisScore,
            MatchingSkills = matchingSkills,
            ImplicitlyDiscoveredSkills = implicitlyMatched,
            BridgeableSkills = bridgeable,
            PrerequisiteMetSkills = prerequisiteMet,
            HardGaps = hardGaps
        };
    }

    private static (string importance, double years) GetJobSkillData(ARIS.Shared.Models.CleanSignal.JobPostingCleanSignal signal, string skillName)
    {
        var jobSkill = signal.RequiredSkills
            .FirstOrDefault(js => js.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
        return (jobSkill?.Importance ?? "Essential", jobSkill?.YearsOfExperience ?? 0);
    }

    private static SkillGapItem BuildSkillGapItem(
        string skillName,
        string importance,
        double years,
        Dictionary<string, (string ViaSkill, string BridgeType, string? BridgeSource)> bridgePathBySkill)
    {
        string? bridgePath = null;
        string? bridgeSource = null;

        if (bridgePathBySkill.TryGetValue(skillName, out var pathInfo))
        {
            bridgePath = $"via {pathInfo.ViaSkill} ({pathInfo.BridgeType})";
            bridgeSource = pathInfo.BridgeSource;
        }

        return new SkillGapItem
        {
            SkillName = skillName,
            Importance = importance,
            YearsRequired = years,
            BridgePath = bridgePath,
            BridgeSource = bridgeSource
        };
    }

    /// <summary>
    /// B3: Generates a grounded match summary using graph-path context injected into the LLM prompt.
    /// Returns the narrative summary and its graph grounding score.
    /// </summary>
    public async Task<(string Summary, double GroundingScore)> GenerateGroundedSummaryAsync(Guid userProfileId, Guid jobId)
    {
        var analysis = await AnalyzeMatchAsync(userProfileId, jobId);
        if (analysis == null)
            return ("Match analysis could not be performed.", 0.0);

        var user = await _context.UserProfiles.FindAsync(userProfileId);
        var job = await _context.JobPostings.FindAsync(jobId);
        if (user?.CleanSignal == null || job?.CleanSignal == null)
            return ("Profile data unavailable.", 0.0);

        var userSkills = user.CleanSignal.Skills.Select(s => s.Name).ToList();
        var jobSkills = job.CleanSignal.RequiredSkills.Select(s => s.Name).ToList();
        var graphContext = await _graphService.GetGraphContextForMatchAsync(userSkills, jobSkills);

        var jobTitle = job.CleanSignal.TargetRoles.FirstOrDefault()?.Title ?? "the role";
        var candidateTitle = user.CleanSignal.Roles.FirstOrDefault()?.Title ?? "the candidate";

        var sb = new StringBuilder();
        sb.AppendLine($"You are a career advisor. Provide a concise 2-3 paragraph assessment of how well {candidateTitle} matches {jobTitle}.");
        sb.AppendLine();
        sb.AppendLine($"MATCHING SKILLS: {string.Join(", ", analysis.MatchingSkills)}");
        sb.AppendLine($"IMPLICIT SKILLS (auto-granted via expertise): {string.Join(", ", analysis.ImplicitlyDiscoveredSkills)}");
        sb.AppendLine($"BRIDGEABLE SKILLS (transferable): {string.Join(", ", analysis.BridgeableSkills.Select(s => s.SkillName))}");
        sb.AppendLine($"HARD GAPS (missing): {string.Join(", ", analysis.HardGaps.Select(s => s.SkillName))}");
        sb.AppendLine();
        sb.AppendLine("KNOWLEDGE GRAPH CONTEXT (verified skill relationships):");
        sb.AppendLine(graphContext);
        sb.AppendLine();
        sb.AppendLine("Use only the above verified information. Do not invent skills or relationships not listed.");

        string summary;
        try
        {
            var response = await _chatClient.GetResponseAsync(sb.ToString());
            summary = response?.Text?.Trim() ?? "Summary unavailable.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate grounded summary.");
            summary = "Summary generation failed.";
        }

        var groundingResult = await _groundingService.CalculateGroundingScoreAsync(summary, userSkills);
        return (summary, groundingResult.Score);
    }

    /// <summary>
    /// Ranks a list of jobs for a given user by vector similarity, with a CreatedAt tiebreak.
    /// </summary>
    public async Task<List<(Guid JobId, double Score)>> RankJobsForUserAsync(Guid userProfileId, IEnumerable<Guid> jobIds)
    {
        var user = await _context.UserProfiles.FindAsync(userProfileId);
        if (user?.Embedding == null) return [];

        var jobs = await _context.JobPostings
            .Where(j => jobIds.Contains(j.Id) && j.Embedding != null)
            .Select(j => new { j.Id, j.CreatedAt, Distance = j.Embedding!.CosineDistance(user.Embedding) })
            .ToListAsync();

        var scores = jobs.Select(j => new { JobId = j.Id, Score = 1.0 - j.Distance, j.CreatedAt }).ToList();

        const double epsilon = 0.001;

        var ranked = scores
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x =>
                scores.Any(y => Math.Abs(y.Score - x.Score) < epsilon && y.JobId != x.JobId)
                    ? x.CreatedAt
                    : DateTime.MinValue)
            .Select(x => (x.JobId, x.Score))
            .ToList();

        return ranked;
    }

    public async Task<object> DebugGetJobsAsync()
    {
        var jobs = await _context.JobPostings
            .OrderByDescending(j => j.CreatedAt)
            .Take(10)
            .ToListAsync();

        return jobs.Select(j => new {
            j.Id,
            Recruiter = j.RecruiterId,
            SampleRole = j.CleanSignal?.TargetRoles?.FirstOrDefault()?.Title ?? "Unknown",
            HasEmbedding = j.Embedding != null
        });
    }

    public async Task<object> DebugGetUsersAsync()
    {
        var users = await _context.UserProfiles
            .OrderByDescending(u => u.UpdatedAt)
            .Take(10)
            .ToListAsync();

        return users.Select(u => new {
            u.Id,
            u.UserId,
            SampleRole = u.CleanSignal?.Roles?.FirstOrDefault()?.Title ?? "Unknown",
            HasEmbedding = u.Embedding != null
        });
    }

    public async Task<object?> DebugGetUserDetailAsync(Guid id)
    {
        var user = await _context.UserProfiles.FindAsync(id);
        if (user == null) return null;
        return new { user.Id, user.UserId, user.CleanSignal };
    }

    public async Task<object?> DebugGetJobDetailAsync(Guid id)
    {
        var job = await _context.JobPostings.FindAsync(id);
        if (job == null) return null;
        return new { job.Id, job.RecruiterId, job.CleanSignal };
    }
}
