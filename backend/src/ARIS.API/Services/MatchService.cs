using ARIS.Shared.Data;
using ARIS.Shared.Helpers;
using ARIS.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ARIS.API.Services;

public record RecruiterSummaryResult(string Summary, double GroundingScore, string Verdict);

public record TailorVerificationResult(
    double BaselineScore,
    double VerifiedScore,
    double Delta,
    List<string> ArticulatedSkills,
    List<string> Hallucinations,
    int HallucinationCount
);

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

        var candidatePrimaryRole = user.CleanSignal?.Roles?.FirstOrDefault(r => r.IsCurrent)
                                   ?? user.CleanSignal?.Roles?.FirstOrDefault();
        bool isCandidateTech = DomainClassifier.IsTechDomain(
            candidatePrimaryRole?.OnetCode, candidatePrimaryRole?.Title);

        var implicitSkills = await _graphService.GetImplicitlyDiscoveredSkillsAsync(userSkills, isTech);

        var totalUserSkills = new HashSet<string>(userSkills, StringComparer.OrdinalIgnoreCase);
        foreach (var s in implicitSkills) totalUserSkills.Add(s);

        var matchingSkills = jobSkills.Intersect(userSkills, StringComparer.OrdinalIgnoreCase).ToList();

        var matchingSkillItems = matchingSkills.Select(skill =>
        {
            var (importance, requiredYears) = GetJobSkillData(job.CleanSignal!, skill);
            var candidateYears = GetCandidateSkillYears(skill, user.CleanSignal!);
            return new SkillGapItem
            {
                SkillName = skill,
                Importance = importance,
                YearsRequired = requiredYears,
                CandidateYears = candidateYears
            };
        }).ToList();

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

        var bridgeable = new List<SkillGapItem>();
        var prerequisiteMet = new List<SkillGapItem>();
        var hardGaps = new List<SkillGapItem>();

        if (missingSkills.Count > 0)
        {
            var neighborhood = await _graphService.GetValidNeighborhoodAsync(totalUserSkills, isTech);
            var prerequisiteMetSet = await _graphService.GetPrerequisiteMetSkillsAsync(totalUserSkills, missingSkills, isTech);

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

            var bridgePaths = await _graphService.GetBridgeablePathsAsync(totalUserSkills, missingSkills);
            var bridgePathBySkill = bridgePaths.ToDictionary(
                p => p.SkillName,
                p => (p.ViaSkill, p.BridgeType, p.BridgeSource),
                StringComparer.OrdinalIgnoreCase);

            foreach (var skill in missingSkills)
            {
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
            (matchingSkillItems.Sum(s => GetImportanceWeight(s.SkillName) * 1.0 * ExperienceMultiplier(s.CandidateYears, s.YearsRequired)) +
             implicitlyMatched.Sum(s => GetImportanceWeight(s) * 0.8) +
             prerequisiteMet.Sum(s => GetImportanceWeight(s.SkillName) * 0.6) +
             bridgeable.Sum(s => GetImportanceWeight(s.SkillName) * 0.4)) / weightedJobTotal,
            1.0);
        var arisScore = 0.40 * similarity + 0.60 * graphCoverageScore;

        static string Normalize(string s) =>
            Regex.Replace(s.ToLowerInvariant().Trim(), @"\.(js|ts|py|net|rb|go)$", "");

        var resumeUngrounded = user.CleanSignal?.UngroundedSkills ?? [];
        var jobUngrounded    = job.CleanSignal?.UngroundedSkills ?? [];

        var resumeMap = resumeUngrounded
            .GroupBy(s => Normalize(s.Name))
            .ToDictionary(g => g.Key, g => g.First().Name);
        var jobMap = jobUngrounded
            .GroupBy(s => Normalize(s.Name))
            .ToDictionary(g => g.Key, g => g.First().Name);

        var ungroundedComparison = new UngroundedSkillComparison
        {
            Matched           = jobMap.Keys.Intersect(resumeMap.Keys)
                                      .Select(k => jobMap[k]).Order().ToList(),
            MissingFromResume = jobMap.Keys.Except(resumeMap.Keys)
                                      .Select(k => jobMap[k]).Order().ToList(),
            ExtraInResume     = resumeMap.Keys.Except(jobMap.Keys)
                                      .Select(k => resumeMap[k]).Order().ToList(),
        };

        return new MatchAnalysisResult
        {
            JobId = job.Id,
            VectorSimilarity = similarity,
            ArisScore = arisScore,
            MatchingSkills = matchingSkillItems,
            ImplicitlyDiscoveredSkills = implicitlyMatched,
            BridgeableSkills = bridgeable,
            PrerequisiteMetSkills = prerequisiteMet,
            HardGaps = hardGaps,
            UngroundedComparison = ungroundedComparison
        };
    }

    private static double GetCandidateSkillYears(
        string skillName,
        ARIS.Shared.Models.CleanSignal.ResumeCleanSignal signal)
    {
        var skill = signal.Skills
            .FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
        return skill?.YearsOfExperience ?? 0;
    }

    private static double ExperienceMultiplier(double candidateYears, double requiredYears)
    {
        if (requiredYears <= 0) return 1.0;
        if (candidateYears >= requiredYears) return 1.0;
        return Math.Max(0.5, candidateYears / requiredYears);
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
    /// Scores a tailored resume's canonical skill output against the baseline match tier classification.
    /// Liberal scoring: T2/T3/T4 skills that appear explicitly in tailored text upgrade to full T1 weight.
    /// T5 (hard gap) skills in tailored text score 0.0 and are logged as hallucinations.
    /// </summary>
    public TailorVerificationResult VerifiedMatchScore(
        MatchAnalysisResult baselineMatch,
        IEnumerable<string> tailoredCanonicalSkills,
        ARIS.Shared.Models.CleanSignal.JobPostingCleanSignal jobSignal)
    {
        var tailoredSet = new HashSet<string>(tailoredCanonicalSkills, StringComparer.OrdinalIgnoreCase);
        var hardGapSet = baselineMatch.HardGaps.Select(s => s.SkillName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Build importance weight map
        var importanceWeights = jobSignal.RequiredSkills
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Any(s => !s.Importance.Equals("Preferred", StringComparison.OrdinalIgnoreCase)) ? 1.0 : 0.6,
                StringComparer.OrdinalIgnoreCase);

        double GetWeight(string skillName) =>
            importanceWeights.TryGetValue(skillName, out var w) ? w : 1.0;

        var weightedJobTotal = Math.Max(importanceWeights.Values.Sum(), 1.0);

        // --- Baseline score: recompute GraphCoverageScore from tier data ---
        double baselineScore =
            (baselineMatch.MatchingSkills.Sum(s => GetWeight(s.SkillName) * 1.0 * ExperienceMultiplier(s.CandidateYears, s.YearsRequired)) +
             baselineMatch.ImplicitlyDiscoveredSkills.Sum(s => GetWeight(s) * 0.8) +
             baselineMatch.PrerequisiteMetSkills.Sum(s => GetWeight(s.SkillName) * 0.6) +
             baselineMatch.BridgeableSkills.Sum(s => GetWeight(s.SkillName) * 0.4)) / weightedJobTotal;
        baselineScore = Math.Min(baselineScore, 1.0);

        // --- Verified score: conservative-floor + articulation-bonus mode ---
        // T1 skills: always count at their full baseline weight — the candidate already has them and
        //   tailoring cannot remove them. If verbatim canonical name also appears in tailored text,
        //   the skill is logged as "confirmed" (no extra score; it was already counted).
        // T2/T3/T4 skills: upgrade from their tier weight to full T1 weight only when the exact
        //   canonical name appears in the tailored text (genuine articulation bonus).
        // T5 skills: 0.0 always; logged as hallucination if they appear in the tailored text.
        // This guarantees verifiedScore >= baselineScore for same-domain pairs (the floor invariant).

        var articulatedSkills = new List<string>();
        var hallucinations = new List<string>();
        double verifiedScore = 0.0;

        // T1 matching — unconditionally carry full baseline weight; no dependency on tailored text.
        // Tailoring improves framing, it does not erase real skills the candidate already holds.
        foreach (var skill in baselineMatch.MatchingSkills)
        {
            verifiedScore += GetWeight(skill.SkillName) * 1.0 * ExperienceMultiplier(skill.CandidateYears, skill.YearsRequired);
        }

        // T2 implicit — baseline contributes 0.8 weight; upgrade to 1.0 if articulated in tailored text.
        foreach (var skill in baselineMatch.ImplicitlyDiscoveredSkills)
        {
            if (tailoredSet.Contains(skill))
            {
                // Articulation bonus: upgrade from 0.8 to 1.0 (add the extra 0.2)
                verifiedScore += GetWeight(skill) * 0.2;
                articulatedSkills.Add(skill);
            }
            else
            {
                // Not articulated: keep baseline tier weight so verifiedScore >= baselineScore
                verifiedScore += GetWeight(skill) * 0.8;
            }
        }

        // T3 prereq met — baseline contributes 0.6 weight; upgrade to 1.0 if articulated.
        foreach (var skill in baselineMatch.PrerequisiteMetSkills)
        {
            if (tailoredSet.Contains(skill.SkillName))
            {
                // Articulation bonus: upgrade from 0.6 to 1.0 (add the extra 0.4)
                verifiedScore += GetWeight(skill.SkillName) * 0.4;
                articulatedSkills.Add(skill.SkillName);
            }
            else
            {
                verifiedScore += GetWeight(skill.SkillName) * 0.6;
            }
        }

        // T4 bridgeable — baseline contributes 0.4 weight; upgrade to 1.0 if articulated.
        foreach (var skill in baselineMatch.BridgeableSkills)
        {
            if (tailoredSet.Contains(skill.SkillName))
            {
                // Articulation bonus: upgrade from 0.4 to 1.0 (add the extra 0.6)
                verifiedScore += GetWeight(skill.SkillName) * 0.6;
                articulatedSkills.Add(skill.SkillName);
            }
            else
            {
                verifiedScore += GetWeight(skill.SkillName) * 0.4;
            }
        }

        // T5 hard gaps — hallucination if present in tailored text
        foreach (var skill in baselineMatch.HardGaps)
        {
            if (tailoredSet.Contains(skill.SkillName))
                hallucinations.Add(skill.SkillName);
        }

        verifiedScore = Math.Min(verifiedScore / weightedJobTotal, 1.0);
        double delta = baselineScore > 0 ? (verifiedScore - baselineScore) / baselineScore : 0.0;

        return new TailorVerificationResult(
            BaselineScore: Math.Round(baselineScore, 4),
            VerifiedScore: Math.Round(verifiedScore, 4),
            Delta: Math.Round(delta, 4),
            ArticulatedSkills: articulatedSkills,
            Hallucinations: hallucinations,
            HallucinationCount: hallucinations.Count
        );
    }

    /// <summary>
    /// Extracts canonical skill names present in the tailored resume output (bullets + summary).
    /// Primary pass: word-boundary substring matching (same as GroundingService.ExtractSkillsFromText).
    /// Secondary pass: for short skill names (length &lt;= 6, e.g. "GAAP", "SQL", "CPA", "R"),
    /// also accepts a plain case-insensitive Contains match to handle punctuation-adjacent occurrences
    /// that the boundary check would reject (e.g. "GAAP," or "(SQL)").
    /// </summary>
    public async Task<HashSet<string>> ExtractCanonicalSkillsFromTailoredTextAsync(
        IEnumerable<ARIS.Shared.Models.TailoredBullet> bullets,
        string summary)
    {
        var fullText = string.Join(" ", bullets.Select(b => b.RewrittenBullet)) + " " + summary;

        var allSkillNames = await _context.Skills.Select(s => s.Name).ToListAsync();

        var lowerText = fullText.ToLowerInvariant();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            {
                found.Add(skillName);
            }
            else if (lowerSkill.Length <= 6)
            {
                // Secondary pass for short abbreviations: accept if surrounded only by non-alpha chars
                // (punctuation, whitespace, parens). Rejects false positives like "r" matching "report".
                bool startOk = !char.IsLetter(charBefore);
                bool endOk = !char.IsLetter(charAfter);
                if (startOk && endOk)
                    found.Add(skillName);
            }
        }

        return found;
    }

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
        sb.AppendLine($"MATCHING SKILLS: {string.Join(", ", analysis.MatchingSkills.Select(s => s.SkillName))}");
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

    public async Task<RecruiterSummaryResult> GenerateRecruiterSummaryAsync(Guid userProfileId, Guid jobId)
    {
        var analysis = await AnalyzeMatchAsync(userProfileId, jobId);
        if (analysis == null)
            return new RecruiterSummaryResult("Match analysis could not be performed.", 0.0, "Not Recommended");

        var user = await _context.UserProfiles.FindAsync(userProfileId);
        var job = await _context.JobPostings.FindAsync(jobId);
        if (user?.CleanSignal == null || job?.CleanSignal == null)
            return new RecruiterSummaryResult("Profile data unavailable.", 0.0, "Not Recommended");

        var userSkills = user.CleanSignal.Skills.Select(s => s.Name).ToList();
        var jobSkills = job.CleanSignal.RequiredSkills.Select(s => s.Name).ToList();
        var graphContext = await _graphService.GetGraphContextForMatchAsync(userSkills, jobSkills);

        var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "RecruiterSummary.md");
        var template = await File.ReadAllTextAsync(promptPath);

        var arisScorePct = (int)Math.Round(analysis.ArisScore * 100);
        var matchingList   = analysis.MatchingSkills.Select(s => s.SkillName).ToList();
        var implicitList   = analysis.ImplicitlyDiscoveredSkills;
        var prereqList     = analysis.PrerequisiteMetSkills.Select(s => s.SkillName).ToList();
        var bridgeList     = analysis.BridgeableSkills.Select(s => s.SkillName).ToList();
        var hardGapList    = analysis.HardGaps.Select(s => s.SkillName).ToList();

        var prompt = template
            .Replace("{arisScore}",       arisScorePct.ToString())
            .Replace("{t1Count}",         matchingList.Count.ToString())
            .Replace("{t2Count}",         implicitList.Count.ToString())
            .Replace("{t3Count}",         prereqList.Count.ToString())
            .Replace("{t4Count}",         bridgeList.Count.ToString())
            .Replace("{t5Count}",         hardGapList.Count.ToString())
            .Replace("{candidateSkills}", string.Join(", ", userSkills))
            .Replace("{jobSkills}",       string.Join(", ", jobSkills))
            .Replace("{matchingSkills}",  matchingList.Count > 0 ? string.Join(", ", matchingList) : "none")
            .Replace("{implicitSkills}",  implicitList.Count > 0 ? string.Join(", ", implicitList) : "none")
            .Replace("{prereqMetSkills}", prereqList.Count  > 0 ? string.Join(", ", prereqList)    : "none")
            .Replace("{bridgeableSkills}",bridgeList.Count  > 0 ? string.Join(", ", bridgeList)    : "none")
            .Replace("{hardGaps}",        hardGapList.Count > 0 ? string.Join(", ", hardGapList)   : "none")
            .Replace("{graphContext}",    graphContext);

        string fullResponse;
        try
        {
            var response = await _chatClient.GetResponseAsync(prompt);
            fullResponse = response?.Text?.Trim() ?? "Summary unavailable.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate recruiter summary.");
            fullResponse = "Summary generation failed.";
        }

        string verdict;
        if (analysis.ArisScore >= 0.75 && hardGapList.Count <= 1)
            verdict = "Strong Fit";
        else if (analysis.ArisScore >= 0.55 && hardGapList.Count <= 3)
            verdict = "Potential Fit";
        else
            verdict = "Not Recommended";

        var lines = fullResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var summaryText = string.Join('\n',
            lines.Where(l => !l.Trim().StartsWith("Verdict:", StringComparison.OrdinalIgnoreCase))).Trim();

        var groundingResult = await _groundingService.CalculateGroundingScoreAsync(summaryText, userSkills);
        return new RecruiterSummaryResult(summaryText, groundingResult.Score, verdict);
    }

    public record CandidateScoreEntry(Guid UserProfileId, string UserId, string PrimaryRole, double FastArisScore);
    public record JobScoreEntry(Guid JobId, string Title, double FastArisScore);

    public async Task<List<CandidateScoreEntry>> GetFastScoresCandidatesAsync(Guid jobId, int limit)
    {
        var job = await _context.JobPostings.FindAsync(jobId);
        if (job?.Embedding == null || job.CleanSignal == null) return [];

        var profiles = await _context.UserProfiles
            .Where(u => u.Embedding != null && u.CleanSignal != null)
            .OrderBy(u => u.Embedding!.CosineDistance(job.Embedding))
            .Take(limit)
            .ToListAsync();

        var jobVec = job.Embedding.ToArray();
        var jobSkills = job.CleanSignal.RequiredSkills;

        var results = new List<CandidateScoreEntry>(profiles.Count);
        foreach (var profile in profiles)
        {
            var score = ComputeFastArisScore(profile.Embedding!.ToArray(), jobVec,
                profile.CleanSignal!.Skills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
                jobSkills);
            var primaryRole = profile.CleanSignal.Roles?.FirstOrDefault(r => r.IsCurrent)?.Title
                           ?? profile.CleanSignal.Roles?.FirstOrDefault()?.Title
                           ?? "Candidate";
            results.Add(new CandidateScoreEntry(profile.Id, profile.UserId, primaryRole, Math.Round(score, 4)));
        }

        return [.. results.OrderByDescending(r => r.FastArisScore)];
    }

    public async Task<List<JobScoreEntry>> GetFastScoresJobsAsync(Guid profileId, int limit)
    {
        var profile = await _context.UserProfiles.FindAsync(profileId);
        if (profile?.Embedding == null || profile.CleanSignal == null) return [];

        var jobs = await _context.JobPostings
            .Where(j => j.Embedding != null && j.CleanSignal != null)
            .OrderBy(j => j.Embedding!.CosineDistance(profile.Embedding))
            .Take(limit)
            .ToListAsync();

        var profileVec = profile.Embedding.ToArray();
        var candidateSkills = profile.CleanSignal.Skills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<JobScoreEntry>(jobs.Count);
        foreach (var job in jobs)
        {
            var score = ComputeFastArisScore(profileVec, job.Embedding!.ToArray(),
                candidateSkills, job.CleanSignal!.RequiredSkills);
            var title = job.CleanSignal.TargetRoles?.FirstOrDefault()?.Title ?? "Job Posting";
            results.Add(new JobScoreEntry(job.Id, title, Math.Round(score, 4)));
        }

        return [.. results.OrderByDescending(r => r.FastArisScore)];
    }

    private static double ComputeFastArisScore(
        float[] userVec, float[] jobVec,
        HashSet<string> candidateSkills,
        IEnumerable<ARIS.Shared.Models.CleanSignal.JobSkill> jobSkills)
    {
        double dot = 0, normU = 0, normJ = 0;
        for (int i = 0; i < userVec.Length; i++)
        {
            dot   += userVec[i] * jobVec[i];
            normU += userVec[i] * userVec[i];
            normJ += jobVec[i] * jobVec[i];
        }
        double vectorSimilarity = (normU > 0 && normJ > 0) ? dot / (Math.Sqrt(normU) * Math.Sqrt(normJ)) : 0;

        double weightedMatches = 0, weightedTotal = 0;
        foreach (var rs in jobSkills)
        {
            double w = rs.Importance?.ToLowerInvariant() == "essential" ? 1.0 : 0.6;
            weightedTotal += w;
            if (candidateSkills.Contains(rs.Name))
                weightedMatches += w;
        }

        double graphCoverage = weightedTotal > 0 ? weightedMatches / weightedTotal : 0;
        return 0.40 * vectorSimilarity + 0.60 * graphCoverage;
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
        return new { job.Id, job.RecruiterId, job.CleanSignal, job.SourceUrl };
    }
}
