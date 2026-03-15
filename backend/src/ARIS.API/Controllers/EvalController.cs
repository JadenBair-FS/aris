using ARIS.API.Services;
using ARIS.Shared.Data;
using ARIS.Shared.Models;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Text.Json;

namespace ARIS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EvalController : ControllerBase
{
    private readonly MatchService _matchService;
    private readonly ArisDbContext _context;
    private readonly IChatClient _chatClient;
    private readonly IChatClient? _openAiClient;
    private readonly ILogger<EvalController> _logger;
    private readonly ResumeService _resumeService;
    private readonly ResumePdfService _resumePdfService;
    private readonly GraphService _graphService;
    private readonly string _chatGptModel;

    public EvalController(
        MatchService matchService,
        ArisDbContext context,
        IChatClient chatClient,
        ILogger<EvalController> logger,
        ResumeService resumeService,
        ResumePdfService resumePdfService,
        GraphService graphService,
        IServiceProvider serviceProvider,
        IConfiguration configuration)
    {
        _matchService    = matchService;
        _context         = context;
        _chatClient      = chatClient;
        _logger          = logger;
        _resumeService   = resumeService;
        _resumePdfService = resumePdfService;
        _graphService    = graphService;
        _openAiClient    = serviceProvider.GetKeyedService<IChatClient>("openai");
        _chatGptModel    = configuration["OpenAI:ChatGptBaselineModel"] ?? "gpt-4o-mini";
    }

    // Tailor Delta (single ARIS pipeline, supplemental)

    public class TailorDeltaRequest
    {
        public Guid ResumeId { get; set; }
        public Guid JobId { get; set; }
    }

    [HttpPost("tailor-delta")]
    public async Task<IActionResult> TailorDelta([FromBody] TailorDeltaRequest request)
    {
        if (request.ResumeId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("ResumeId and JobId are required.");

        var sw = Stopwatch.StartNew();

        var baselineMatch = await _matchService.AnalyzeMatchAsync(request.ResumeId, request.JobId);
        if (baselineMatch == null)
            return NotFound("Match analysis failed. Ensure both IDs are valid and fully processed.");

        var job = await _context.JobPostings.FindAsync(request.JobId);
        if (job?.CleanSignal == null)
            return NotFound($"Job {request.JobId} not found or has no CleanSignal.");

        var user = await _context.UserProfiles.FindAsync(request.ResumeId);

        string rawResumeText;
        try
        {
            using var doc = JsonDocument.Parse(user?.RawResume ?? "{}");
            rawResumeText = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : user?.RawResume ?? "";
        }
        catch { rawResumeText = user?.RawResume ?? ""; }
        var rawJobText = job.RawDescription ?? "";

        var tailoredData = await _resumeService.BuildTailoredResumeDataAsync(
            request.ResumeId, request.JobId, precomputedMatch: baselineMatch);
        if (tailoredData == null)
            return StatusCode(500, "Tailoring pipeline failed.");

        var pdfBytes = _resumePdfService.GeneratePdf(
            tailoredData.PersonalInfo,
            tailoredData.CleanSignal,
            tailoredData.ProfessionalSummary,
            tailoredData.TailoredBullets);

        var originalMappings = BuildOriginalMappings(tailoredData.CleanSignal, job.CleanSignal);
        var canonicalSkills = await _matchService.ExtractCanonicalSkillsFromTailoredTextAsync(
            tailoredData.TailoredBullets, tailoredData.ProfessionalSummary ?? "", originalMappings);
        var scoring = _matchService.VerifiedMatchScore(baselineMatch, canonicalSkills, job.CleanSignal);

        var summarySemanticScore    = await ComputeSemanticSimilarityAsync(tailoredData.ProfessionalSummary ?? "", rawJobText);
        var summarySemanticBaseline = await ComputeSemanticSimilarityAsync(rawResumeText, rawJobText);
        var summaryBridgeRate       = await ComputeSummaryBridgeRate(
            tailoredData.ProfessionalSummary ?? "", baselineMatch, tailoredData.CleanSignal, job.CleanSignal);

        sw.Stop();

        return Ok(new
        {
            resumeId                    = request.ResumeId,
            jobId                       = request.JobId,
            latencyMs                   = sw.ElapsedMilliseconds,
            summaryBridgeRate,
            summarySemanticScore,
            summarySemanticBaseline,
            summarySemanticDeltaPercent = summarySemanticBaseline > 0
                ? Math.Round((summarySemanticScore - summarySemanticBaseline) / summarySemanticBaseline * 100.0, 2)
                : 0.0,
            hallucinationCount          = scoring.HallucinationCount,
            hallucinations              = scoring.Hallucinations,
            baselineGraphScore          = scoring.BaselineScore,
            tailoredGraphScore          = scoring.VerifiedScore,
            graphScoreDeltaPercent      = Math.Round(scoring.Delta * 100.0, 2),
            summaryText                 = tailoredData.ProfessionalSummary,
            pdfBase64                   = Convert.ToBase64String(pdfBytes)
        });
    }

    // Three-Way Compare (Original vs ARIS GraphRAG vs ChatGPT)

    public class ThreeWayCompareRequest
    {
        public Guid ResumeId { get; set; }
        public Guid JobId { get; set; }
    }

    public class SkillMatchDetail
    {
        public string CanonicalName { get; set; } = "";
        public string? OriginalName { get; set; }
        public string? MatchedOn { get; set; }  // which name triggered the hit (null if missing)
    }

    public class IdentifiedSkillDetail
    {
        public string CanonicalName { get; set; } = "";
        public string? OriginalName { get; set; }
        public string Tier { get; set; } = "";       // "T1", "T2", "T3", "T4", "T5"
        public string TierLabel { get; set; } = "";  // "Direct Match", "Foundation", etc.
        public string? BridgePath { get; set; }
        public string Importance { get; set; } = "";
    }

    public class ThreeWayResult
    {
        public double SemanticSimilarityScore { get; set; }
        public double KeywordMatchScore { get; set; }
        public double AtsScore { get; set; }
        public int MatchingTermsCount { get; set; }
        public int MissingTermsCount { get; set; }
        public List<SkillMatchDetail> MatchingTerms { get; set; } = [];
        public List<SkillMatchDetail> MissingTerms { get; set; } = [];
        public List<SkillMatchDetail> NewTermsAdded { get; set; } = [];
        public int HallucinationCount { get; set; }
        public string SummaryText { get; set; } = "";
        public string TailoredFullText { get; set; } = "";
        public long LatencyMs { get; set; }
        public string? PdfBase64 { get; set; }
    }

    [HttpPost("three-way-compare")]
    public async Task<IActionResult> ThreeWayCompare([FromBody] ThreeWayCompareRequest request)
    {
        if (request.ResumeId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("ResumeId and JobId are required.");

        var user = await _context.UserProfiles.FindAsync(request.ResumeId);
        var job  = await _context.JobPostings.FindAsync(request.JobId);

        if (user?.CleanSignal == null)
            return NotFound($"User profile {request.ResumeId} not found or has no CleanSignal.");
        if (job?.CleanSignal == null)
            return NotFound($"Job {request.JobId} not found or has no CleanSignal.");

        string rawResumeText;
        try
        {
            using var doc = JsonDocument.Parse(user.RawResume ?? "{}");
            rawResumeText = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : user.RawResume ?? "";
        }
        catch { rawResumeText = user.RawResume ?? ""; }
        var rawJobText = job.RawDescription ?? "";

        var jobTitle  = job.CleanSignal.TargetRoles?.FirstOrDefault()?.Title ?? "the role";
        var jobSkills = job.CleanSignal.RequiredSkills.ToList();

        var baselineMatch = await _matchService.AnalyzeMatchAsync(request.ResumeId, request.JobId);
        if (baselineMatch == null)
            return NotFound("Match analysis failed. Ensure both IDs are valid and fully processed.");

        // Hard-gap terms used by BOTH ARIS and ChatGPT hallucination checks.
        // Includes both canonical name AND the job's original wording so paraphrased
        // forms (e.g. "Async Python" → canonical "Asynchronous Django") are also caught.
        var hardGapNames = BuildHardGapNameSet(baselineMatch.HardGaps);

        var swA = Stopwatch.StartNew();
        var origKeywords = ComputeKeywordMatch(rawResumeText, jobSkills);
        var origSemScore = await ComputeSemanticSimilarityAsync(rawResumeText, rawJobText);
        var origKwScore  = jobSkills.Count > 0
            ? Math.Round((double)origKeywords.Matched.Count / jobSkills.Count, 4) : 0.0;
        swA.Stop();

        var condA = new ThreeWayResult
        {
            SemanticSimilarityScore = origSemScore,
            KeywordMatchScore       = origKwScore,
            AtsScore                = Math.Round(0.5 * origSemScore + 0.5 * origKwScore, 4),
            MatchingTerms           = origKeywords.Matched,
            MissingTerms            = origKeywords.Missing,
            MatchingTermsCount      = origKeywords.Matched.Count,
            MissingTermsCount       = origKeywords.Missing.Count,
            NewTermsAdded           = [],
            HallucinationCount      = 0,
            SummaryText             = "",
            TailoredFullText        = rawResumeText,
            LatencyMs               = swA.ElapsedMilliseconds,
            PdfBase64               = null,
        };

        var swB = Stopwatch.StartNew();
        var tailoredData = await _resumeService.BuildTailoredResumeDataAsync(
            request.ResumeId, request.JobId, precomputedMatch: baselineMatch);
        ThreeWayResult condB;
        if (tailoredData == null)
        {
            swB.Stop();
            _logger.LogError("ARIS tailoring pipeline returned null for resume={ResumeId} job={JobId}",
                request.ResumeId, request.JobId);
            condB = new ThreeWayResult { LatencyMs = swB.ElapsedMilliseconds };
        }
        else
        {
            var pdfBytes = _resumePdfService.GeneratePdf(
                tailoredData.PersonalInfo, tailoredData.CleanSignal,
                tailoredData.ProfessionalSummary, tailoredData.TailoredBullets);

            // Group bullets by experience entry (Role + Company) to produce structured full text.
            // Format mirrors what ChatGptTailoring.md asks GPT to return:
            //   {summary}
            //   {Role} at {Company}
            //   {bullet}
            //   {bullet}
            var entrySections = tailoredData.TailoredBullets
                .Where(b => !string.IsNullOrWhiteSpace(b.RewrittenBullet))
                .GroupBy(b => (b.Role, b.Company))
                .Select(g =>
                {
                    var header = (string.IsNullOrWhiteSpace(g.Key.Role) && string.IsNullOrWhiteSpace(g.Key.Company))
                        ? ""
                        : string.IsNullOrWhiteSpace(g.Key.Company)
                            ? g.Key.Role
                            : $"{g.Key.Role} at {g.Key.Company}";
                    var bullets = string.Join("\n", g.Select(b => b.RewrittenBullet));
                    return string.IsNullOrWhiteSpace(header) ? bullets : $"{header}\n{bullets}";
                });
            var bulletsText = string.Join("\n\n", entrySections);

            // Extract the Skills section from the original resume so keyword matching
            // covers explicit skill names that ARIS does not rewrite.
            var skillsSectionMatch = System.Text.RegularExpressions.Regex.Match(
                rawResumeText,
                @"(?im)^(SKILLS?[^\n]*)\n(.*?)(?=\n[A-Z][A-Z\s]{2,}:?\s*$|\z)",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            var skillsBlock = skillsSectionMatch.Success ? skillsSectionMatch.Value.Trim() : "";

            var arisFullText = string.IsNullOrWhiteSpace(tailoredData.ProfessionalSummary)
                ? (skillsBlock.Length > 0 ? $"{skillsBlock}\n\n{bulletsText}" : bulletsText)
                : (skillsBlock.Length > 0
                    ? $"{tailoredData.ProfessionalSummary}\n\n{skillsBlock}\n\n{bulletsText}"
                    : $"{tailoredData.ProfessionalSummary}\n\n{bulletsText}");

            var arisHallucinationCount = hardGapNames.Count(name =>
                ContainsWholeWord(arisFullText, name));

            var arisKeywords = ComputeKeywordMatch(arisFullText, jobSkills);
            var arisSemScore = await ComputeSemanticSimilarityAsync(arisFullText, rawJobText);
            var arisKwScore  = jobSkills.Count > 0
                ? Math.Round((double)arisKeywords.Matched.Count / jobSkills.Count, 4) : 0.0;
            var origMatchedCanonicalB = new HashSet<string>(
                origKeywords.Matched.Select(m => m.CanonicalName), StringComparer.OrdinalIgnoreCase);
            var arisNewTerms = arisKeywords.Matched
                .Where(m => !origMatchedCanonicalB.Contains(m.CanonicalName))
                .ToList();

            swB.Stop();
            condB = new ThreeWayResult
            {
                SemanticSimilarityScore = arisSemScore,
                KeywordMatchScore       = arisKwScore,
                AtsScore                = Math.Round(0.5 * arisSemScore + 0.5 * arisKwScore, 4),
                MatchingTerms           = arisKeywords.Matched,
                MissingTerms            = arisKeywords.Missing,
                MatchingTermsCount      = arisKeywords.Matched.Count,
                MissingTermsCount       = arisKeywords.Missing.Count,
                NewTermsAdded           = arisNewTerms,
                HallucinationCount      = arisHallucinationCount,
                SummaryText             = tailoredData.ProfessionalSummary ?? "",
                TailoredFullText        = arisFullText,
                LatencyMs               = swB.ElapsedMilliseconds,
                PdfBase64               = Convert.ToBase64String(pdfBytes),
            };
        }

        var swC = Stopwatch.StartNew();
        var (gptFullText, gptSummary) = await RunChatGptPipelineAsync(rawResumeText, rawJobText);
        var gptKeywords = ComputeKeywordMatch(gptFullText, jobSkills);
        var gptSemScore = await ComputeSemanticSimilarityAsync(gptFullText, rawJobText);
        var gptKwScore  = jobSkills.Count > 0
            ? Math.Round((double)gptKeywords.Matched.Count / jobSkills.Count, 4) : 0.0;
        var origMatchedCanonicalC = new HashSet<string>(
            origKeywords.Matched.Select(m => m.CanonicalName), StringComparer.OrdinalIgnoreCase);
        var gptNewTerms = gptKeywords.Matched
            .Where(m => !origMatchedCanonicalC.Contains(m.CanonicalName))
            .ToList();
        var gptHallucinationCount = hardGapNames.Count(name =>
            ContainsWholeWord(gptFullText, name));
        swC.Stop();

        var condC = new ThreeWayResult
        {
            SemanticSimilarityScore = gptSemScore,
            KeywordMatchScore       = gptKwScore,
            AtsScore                = Math.Round(0.5 * gptSemScore + 0.5 * gptKwScore, 4),
            MatchingTerms           = gptKeywords.Matched,
            MissingTerms            = gptKeywords.Missing,
            MatchingTermsCount      = gptKeywords.Matched.Count,
            MissingTermsCount       = gptKeywords.Missing.Count,
            NewTermsAdded           = gptNewTerms,
            HallucinationCount      = gptHallucinationCount,
            SummaryText             = gptSummary,
            TailoredFullText        = gptFullText,
            LatencyMs               = swC.ElapsedMilliseconds,
            PdfBase64               = null,
        };

        var identifiedSkills = new List<IdentifiedSkillDetail>();

        foreach (var s in baselineMatch.MatchingSkills)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, OriginalName = s.OriginalName,
                Tier = "T1", TierLabel = "Direct Match", Importance = s.Importance });

        foreach (var s in baselineMatch.ImplicitlyDiscoveredSkills)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, Tier = "T2", TierLabel = "Foundation (SUBSET_OF)" });

        foreach (var s in baselineMatch.PrerequisiteMetSkills)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, OriginalName = s.OriginalName,
                Tier = "T3", TierLabel = "Prerequisite Met", BridgePath = s.BridgePath, Importance = s.Importance });

        foreach (var s in baselineMatch.BridgeableSkills)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, OriginalName = s.OriginalName,
                Tier = "T4", TierLabel = "Bridgeable (IS_SIMILAR_TO)", BridgePath = s.BridgePath, Importance = s.Importance });

        foreach (var s in baselineMatch.HardGaps)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, OriginalName = s.OriginalName,
                Tier = "T5", TierLabel = "Hard Gap", Importance = s.Importance });

        return Ok(new
        {
            resumeId         = request.ResumeId,
            jobId            = request.JobId,
            jobTitle,
            chatGptModel     = _chatGptModel,
            identifiedSkills,
            original         = condA,
            arisGraphRag     = condB,
            chatGptBaseline  = condC,
        });
    }

    private async Task<(string TailoredFullText, string SummaryText)> RunChatGptPipelineAsync(
        string rawResume, string rawJob)
    {
        if (_openAiClient == null)
            return ("ChatGPT baseline unavailable: OPENAI_API_KEY is not configured.", "");

        var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "ChatGptTailoring.md");
        string promptTemplate;
        try { promptTemplate = await System.IO.File.ReadAllTextAsync(promptPath); }
        catch { promptTemplate = "Rewrite the following resume to better match the job description. Return plain text only.\n\nResume:\n{rawResumeText}\n\nJob Description:\n{rawJobText}"; }

        var prompt = promptTemplate
            .Replace("{rawResumeText}", rawResume)
            .Replace("{rawJobText}", rawJob);

        try
        {
            var response = await _openAiClient.GetResponseAsync(prompt);
            var fullText = response?.Text?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(fullText))
                return ("", "");

            // Extract summary: find the paragraph that follows the "SUMMARY" header.
            // The prompt asks for: "SUMMARY\n{summary text}\n\n{Role} at {Company}\n..."
            var summaryText = "";
            var lines = fullText.Split('\n');
            var summaryStart = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Equals("SUMMARY", StringComparison.OrdinalIgnoreCase))
                {
                    summaryStart = i + 1;
                    break;
                }
            }
            if (summaryStart >= 0)
            {
                var summaryLines = new List<string>();
                for (int i = summaryStart; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) break;
                    summaryLines.Add(lines[i].Trim());
                }
                summaryText = string.Join(" ", summaryLines).Trim();
            }
            else
            {
                // Fallback: if no SUMMARY header, treat first non-empty paragraph as summary.
                var paraLines = new List<string>();
                bool started = false;
                foreach (var line in lines)
                {
                    if (!started && string.IsNullOrWhiteSpace(line)) continue;
                    started = true;
                    if (string.IsNullOrWhiteSpace(line)) break;
                    paraLines.Add(line.Trim());
                }
                summaryText = string.Join(" ", paraLines).Trim();
            }

            return (fullText, summaryText);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChatGPT pipeline failed; returning empty output");
        }
        return ("", "");
    }

    [HttpPost("mistral-compare")]
    public async Task<IActionResult> MistralCompare([FromBody] ThreeWayCompareRequest request)
    {
        if (request.ResumeId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("ResumeId and JobId are required.");

        var user = await _context.UserProfiles.FindAsync(request.ResumeId);
        var job  = await _context.JobPostings.FindAsync(request.JobId);

        if (user?.CleanSignal == null)
            return NotFound($"User profile {request.ResumeId} not found or has no CleanSignal.");
        if (job?.CleanSignal == null)
            return NotFound($"Job {request.JobId} not found or has no CleanSignal.");

        string rawResumeText;
        try
        {
            using var doc = JsonDocument.Parse(user.RawResume ?? "{}");
            rawResumeText = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : user.RawResume ?? "";
        }
        catch { rawResumeText = user.RawResume ?? ""; }
        var rawJobText = job.RawDescription ?? "";

        var jobTitle  = job.CleanSignal.TargetRoles?.FirstOrDefault()?.Title ?? "the role";
        var jobSkills = job.CleanSignal.RequiredSkills.ToList();

        var baselineMatch = await _matchService.AnalyzeMatchAsync(request.ResumeId, request.JobId);
        if (baselineMatch == null)
            return NotFound("Match analysis failed. Ensure both IDs are valid and fully processed.");

        var hardGapNames = new HashSet<string>(
            baselineMatch.HardGaps.Select(s => s.SkillName), StringComparer.OrdinalIgnoreCase);

        var swA = Stopwatch.StartNew();
        var origKeywords = ComputeKeywordMatch(rawResumeText, jobSkills);
        var origSemScore = await ComputeSemanticSimilarityAsync(rawResumeText, rawJobText);
        var origKwScore  = jobSkills.Count > 0
            ? Math.Round((double)origKeywords.Matched.Count / jobSkills.Count, 4) : 0.0;
        swA.Stop();

        var condA = new ThreeWayResult
        {
            SemanticSimilarityScore = origSemScore,
            KeywordMatchScore       = origKwScore,
            AtsScore                = Math.Round(0.5 * origSemScore + 0.5 * origKwScore, 4),
            MatchingTerms           = origKeywords.Matched,
            MissingTerms            = origKeywords.Missing,
            MatchingTermsCount      = origKeywords.Matched.Count,
            MissingTermsCount       = origKeywords.Missing.Count,
            NewTermsAdded           = [],
            HallucinationCount      = 0,
            SummaryText             = "",
            TailoredFullText        = rawResumeText,
            LatencyMs               = swA.ElapsedMilliseconds,
            PdfBase64               = null,
        };

        var swB = Stopwatch.StartNew();
        var tailoredData = await _resumeService.BuildTailoredResumeDataAsync(
            request.ResumeId, request.JobId, precomputedMatch: baselineMatch);
        ThreeWayResult condB;
        if (tailoredData == null)
        {
            swB.Stop();
            _logger.LogError("ARIS tailoring pipeline returned null for resume={ResumeId} job={JobId}",
                request.ResumeId, request.JobId);
            condB = new ThreeWayResult { LatencyMs = swB.ElapsedMilliseconds };
        }
        else
        {
            var pdfBytes = _resumePdfService.GeneratePdf(
                tailoredData.PersonalInfo, tailoredData.CleanSignal,
                tailoredData.ProfessionalSummary, tailoredData.TailoredBullets);

            var entrySections = tailoredData.TailoredBullets
                .Where(b => !string.IsNullOrWhiteSpace(b.RewrittenBullet))
                .GroupBy(b => (b.Role, b.Company))
                .Select(g =>
                {
                    var header = (string.IsNullOrWhiteSpace(g.Key.Role) && string.IsNullOrWhiteSpace(g.Key.Company))
                        ? ""
                        : string.IsNullOrWhiteSpace(g.Key.Company)
                            ? g.Key.Role
                            : $"{g.Key.Role} at {g.Key.Company}";
                    var bullets = string.Join("\n", g.Select(b => b.RewrittenBullet));
                    return string.IsNullOrWhiteSpace(header) ? bullets : $"{header}\n{bullets}";
                });
            var bulletsText = string.Join("\n\n", entrySections);

            var skillsSectionMatch = System.Text.RegularExpressions.Regex.Match(
                rawResumeText,
                @"(?im)^(SKILLS?[^\n]*)\n(.*?)(?=\n[A-Z][A-Z\s]{2,}:?\s*$|\z)",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            var skillsBlock = skillsSectionMatch.Success ? skillsSectionMatch.Value.Trim() : "";

            var arisFullText = string.IsNullOrWhiteSpace(tailoredData.ProfessionalSummary)
                ? (skillsBlock.Length > 0 ? $"{skillsBlock}\n\n{bulletsText}" : bulletsText)
                : (skillsBlock.Length > 0
                    ? $"{tailoredData.ProfessionalSummary}\n\n{skillsBlock}\n\n{bulletsText}"
                    : $"{tailoredData.ProfessionalSummary}\n\n{bulletsText}");

            var arisHallucinationCount = hardGapNames.Count(name =>
                ContainsWholeWord(arisFullText, name));

            var arisKeywords = ComputeKeywordMatch(arisFullText, jobSkills);
            var arisSemScore = await ComputeSemanticSimilarityAsync(arisFullText, rawJobText);
            var arisKwScore  = jobSkills.Count > 0
                ? Math.Round((double)arisKeywords.Matched.Count / jobSkills.Count, 4) : 0.0;
            var origMatchedCanonicalB = new HashSet<string>(
                origKeywords.Matched.Select(m => m.CanonicalName), StringComparer.OrdinalIgnoreCase);
            var arisNewTerms = arisKeywords.Matched
                .Where(m => !origMatchedCanonicalB.Contains(m.CanonicalName))
                .ToList();

            swB.Stop();
            condB = new ThreeWayResult
            {
                SemanticSimilarityScore = arisSemScore,
                KeywordMatchScore       = arisKwScore,
                AtsScore                = Math.Round(0.5 * arisSemScore + 0.5 * arisKwScore, 4),
                MatchingTerms           = arisKeywords.Matched,
                MissingTerms            = arisKeywords.Missing,
                MatchingTermsCount      = arisKeywords.Matched.Count,
                MissingTermsCount       = arisKeywords.Missing.Count,
                NewTermsAdded           = arisNewTerms,
                HallucinationCount      = arisHallucinationCount,
                SummaryText             = tailoredData.ProfessionalSummary ?? "",
                TailoredFullText        = arisFullText,
                LatencyMs               = swB.ElapsedMilliseconds,
                PdfBase64               = Convert.ToBase64String(pdfBytes),
            };
        }

        var swC = Stopwatch.StartNew();
        var (mistralFullText, mistralSummary) = await RunMistralBaselinePipelineAsync(rawResumeText, rawJobText);
        var mistralKeywords = ComputeKeywordMatch(mistralFullText, jobSkills);
        var mistralSemScore = await ComputeSemanticSimilarityAsync(mistralFullText, rawJobText);
        var mistralKwScore  = jobSkills.Count > 0
            ? Math.Round((double)mistralKeywords.Matched.Count / jobSkills.Count, 4) : 0.0;
        var origMatchedCanonicalC = new HashSet<string>(
            origKeywords.Matched.Select(m => m.CanonicalName), StringComparer.OrdinalIgnoreCase);
        var mistralNewTerms = mistralKeywords.Matched
            .Where(m => !origMatchedCanonicalC.Contains(m.CanonicalName))
            .ToList();
        var mistralHallucinationCount = hardGapNames.Count(name =>
            ContainsWholeWord(mistralFullText, name));
        swC.Stop();

        var condC = new ThreeWayResult
        {
            SemanticSimilarityScore = mistralSemScore,
            KeywordMatchScore       = mistralKwScore,
            AtsScore                = Math.Round(0.5 * mistralSemScore + 0.5 * mistralKwScore, 4),
            MatchingTerms           = mistralKeywords.Matched,
            MissingTerms            = mistralKeywords.Missing,
            MatchingTermsCount      = mistralKeywords.Matched.Count,
            MissingTermsCount       = mistralKeywords.Missing.Count,
            NewTermsAdded           = mistralNewTerms,
            HallucinationCount      = mistralHallucinationCount,
            SummaryText             = mistralSummary,
            TailoredFullText        = mistralFullText,
            LatencyMs               = swC.ElapsedMilliseconds,
            PdfBase64               = null,
        };

        var identifiedSkills = new List<IdentifiedSkillDetail>();

        foreach (var s in baselineMatch.MatchingSkills)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, OriginalName = s.OriginalName,
                Tier = "T1", TierLabel = "Direct Match", Importance = s.Importance });

        foreach (var s in baselineMatch.ImplicitlyDiscoveredSkills)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, Tier = "T2", TierLabel = "Foundation (SUBSET_OF)" });

        foreach (var s in baselineMatch.PrerequisiteMetSkills)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, OriginalName = s.OriginalName,
                Tier = "T3", TierLabel = "Prerequisite Met", BridgePath = s.BridgePath, Importance = s.Importance });

        foreach (var s in baselineMatch.BridgeableSkills)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, OriginalName = s.OriginalName,
                Tier = "T4", TierLabel = "Bridgeable (IS_SIMILAR_TO)", BridgePath = s.BridgePath, Importance = s.Importance });

        foreach (var s in baselineMatch.HardGaps)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, OriginalName = s.OriginalName,
                Tier = "T5", TierLabel = "Hard Gap", Importance = s.Importance });

        return Ok(new
        {
            resumeId         = request.ResumeId,
            jobId            = request.JobId,
            jobTitle,
            identifiedSkills,
            original         = condA,
            arisGraphRag     = condB,
            mistralBaseline  = condC,
        });
    }

    [HttpPost("gpt-compare")]
    public async Task<IActionResult> GptCompare([FromBody] ThreeWayCompareRequest request)
    {
        if (_openAiClient == null)
            return StatusCode(503, "GPT compare unavailable: OPENAI_API_KEY is not configured.");

        if (request.ResumeId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("ResumeId and JobId are required.");

        var user = await _context.UserProfiles.FindAsync(request.ResumeId);
        var job  = await _context.JobPostings.FindAsync(request.JobId);

        if (user?.CleanSignal == null)
            return NotFound($"User profile {request.ResumeId} not found or has no CleanSignal.");
        if (job?.CleanSignal == null)
            return NotFound($"Job {request.JobId} not found or has no CleanSignal.");

        string rawResumeText;
        try
        {
            using var doc = JsonDocument.Parse(user.RawResume ?? "{}");
            rawResumeText = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : user.RawResume ?? "";
        }
        catch { rawResumeText = user.RawResume ?? ""; }
        var rawJobText = job.RawDescription ?? "";

        var jobTitle  = job.CleanSignal.TargetRoles?.FirstOrDefault()?.Title ?? "the role";
        var jobSkills = job.CleanSignal.RequiredSkills.ToList();

        var baselineMatch = await _matchService.AnalyzeMatchAsync(request.ResumeId, request.JobId);
        if (baselineMatch == null)
            return NotFound("Match analysis failed. Ensure both IDs are valid and fully processed.");

        var hardGapNames = new HashSet<string>(
            baselineMatch.HardGaps.Select(s => s.SkillName), StringComparer.OrdinalIgnoreCase);

        var swA = Stopwatch.StartNew();
        var origKeywords = ComputeKeywordMatch(rawResumeText, jobSkills);
        var origSemScore = await ComputeSemanticSimilarityAsync(rawResumeText, rawJobText);
        var origKwScore  = jobSkills.Count > 0
            ? Math.Round((double)origKeywords.Matched.Count / jobSkills.Count, 4) : 0.0;
        swA.Stop();

        var condA = new ThreeWayResult
        {
            SemanticSimilarityScore = origSemScore,
            KeywordMatchScore       = origKwScore,
            AtsScore                = Math.Round(0.5 * origSemScore + 0.5 * origKwScore, 4),
            MatchingTerms           = origKeywords.Matched,
            MissingTerms            = origKeywords.Missing,
            MatchingTermsCount      = origKeywords.Matched.Count,
            MissingTermsCount       = origKeywords.Missing.Count,
            NewTermsAdded           = [],
            HallucinationCount      = 0,
            SummaryText             = "",
            TailoredFullText        = rawResumeText,
            LatencyMs               = swA.ElapsedMilliseconds,
            PdfBase64               = null,
        };

        var swB = Stopwatch.StartNew();
        var gptTailoredData = await _resumeService.BuildTailoredResumeDataAsync(
            request.ResumeId, request.JobId, precomputedMatch: baselineMatch, llmClient: _openAiClient);
        ThreeWayResult condB;
        if (gptTailoredData == null)
        {
            swB.Stop();
            _logger.LogError("GPT+GraphRAG tailoring pipeline returned null for resume={ResumeId} job={JobId}",
                request.ResumeId, request.JobId);
            condB = new ThreeWayResult { LatencyMs = swB.ElapsedMilliseconds };
        }
        else
        {
            var pdfBytes = _resumePdfService.GeneratePdf(
                gptTailoredData.PersonalInfo, gptTailoredData.CleanSignal,
                gptTailoredData.ProfessionalSummary, gptTailoredData.TailoredBullets);

            var entrySections = gptTailoredData.TailoredBullets
                .Where(b => !string.IsNullOrWhiteSpace(b.RewrittenBullet))
                .GroupBy(b => (b.Role, b.Company))
                .Select(g =>
                {
                    var header = (string.IsNullOrWhiteSpace(g.Key.Role) && string.IsNullOrWhiteSpace(g.Key.Company))
                        ? ""
                        : string.IsNullOrWhiteSpace(g.Key.Company)
                            ? g.Key.Role
                            : $"{g.Key.Role} at {g.Key.Company}";
                    var bullets = string.Join("\n", g.Select(b => b.RewrittenBullet));
                    return string.IsNullOrWhiteSpace(header) ? bullets : $"{header}\n{bullets}";
                });
            var bulletsText = string.Join("\n\n", entrySections);

            var skillsSectionMatch = System.Text.RegularExpressions.Regex.Match(
                rawResumeText,
                @"(?im)^(SKILLS?[^\n]*)\n(.*?)(?=\n[A-Z][A-Z\s]{2,}:?\s*$|\z)",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            var skillsBlock = skillsSectionMatch.Success ? skillsSectionMatch.Value.Trim() : "";

            var gptGraphFullText = string.IsNullOrWhiteSpace(gptTailoredData.ProfessionalSummary)
                ? (skillsBlock.Length > 0 ? $"{skillsBlock}\n\n{bulletsText}" : bulletsText)
                : (skillsBlock.Length > 0
                    ? $"{gptTailoredData.ProfessionalSummary}\n\n{skillsBlock}\n\n{bulletsText}"
                    : $"{gptTailoredData.ProfessionalSummary}\n\n{bulletsText}");

            var gptGraphHallucinationCount = hardGapNames.Count(name =>
                ContainsWholeWord(gptGraphFullText, name));

            var gptGraphKeywords = ComputeKeywordMatch(gptGraphFullText, jobSkills);
            var gptGraphSemScore = await ComputeSemanticSimilarityAsync(gptGraphFullText, rawJobText);
            var gptGraphKwScore  = jobSkills.Count > 0
                ? Math.Round((double)gptGraphKeywords.Matched.Count / jobSkills.Count, 4) : 0.0;
            var origMatchedCanonicalB = new HashSet<string>(
                origKeywords.Matched.Select(m => m.CanonicalName), StringComparer.OrdinalIgnoreCase);
            var gptGraphNewTerms = gptGraphKeywords.Matched
                .Where(m => !origMatchedCanonicalB.Contains(m.CanonicalName))
                .ToList();

            swB.Stop();
            condB = new ThreeWayResult
            {
                SemanticSimilarityScore = gptGraphSemScore,
                KeywordMatchScore       = gptGraphKwScore,
                AtsScore                = Math.Round(0.5 * gptGraphSemScore + 0.5 * gptGraphKwScore, 4),
                MatchingTerms           = gptGraphKeywords.Matched,
                MissingTerms            = gptGraphKeywords.Missing,
                MatchingTermsCount      = gptGraphKeywords.Matched.Count,
                MissingTermsCount       = gptGraphKeywords.Missing.Count,
                NewTermsAdded           = gptGraphNewTerms,
                HallucinationCount      = gptGraphHallucinationCount,
                SummaryText             = gptTailoredData.ProfessionalSummary ?? "",
                TailoredFullText        = gptGraphFullText,
                LatencyMs               = swB.ElapsedMilliseconds,
                PdfBase64               = Convert.ToBase64String(pdfBytes),
            };
        }

        var swC = Stopwatch.StartNew();
        var (gptFullText, gptSummary) = await RunChatGptPipelineAsync(rawResumeText, rawJobText);
        var gptKeywords = ComputeKeywordMatch(gptFullText, jobSkills);
        var gptSemScore = await ComputeSemanticSimilarityAsync(gptFullText, rawJobText);
        var gptKwScore  = jobSkills.Count > 0
            ? Math.Round((double)gptKeywords.Matched.Count / jobSkills.Count, 4) : 0.0;
        var origMatchedCanonicalC = new HashSet<string>(
            origKeywords.Matched.Select(m => m.CanonicalName), StringComparer.OrdinalIgnoreCase);
        var gptNewTerms = gptKeywords.Matched
            .Where(m => !origMatchedCanonicalC.Contains(m.CanonicalName))
            .ToList();
        var gptHallucinationCount = hardGapNames.Count(name =>
            ContainsWholeWord(gptFullText, name));
        swC.Stop();

        var condC = new ThreeWayResult
        {
            SemanticSimilarityScore = gptSemScore,
            KeywordMatchScore       = gptKwScore,
            AtsScore                = Math.Round(0.5 * gptSemScore + 0.5 * gptKwScore, 4),
            MatchingTerms           = gptKeywords.Matched,
            MissingTerms            = gptKeywords.Missing,
            MatchingTermsCount      = gptKeywords.Matched.Count,
            MissingTermsCount       = gptKeywords.Missing.Count,
            NewTermsAdded           = gptNewTerms,
            HallucinationCount      = gptHallucinationCount,
            SummaryText             = gptSummary,
            TailoredFullText        = gptFullText,
            LatencyMs               = swC.ElapsedMilliseconds,
            PdfBase64               = null,
        };

        var identifiedSkills = new List<IdentifiedSkillDetail>();

        foreach (var s in baselineMatch.MatchingSkills)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, OriginalName = s.OriginalName,
                Tier = "T1", TierLabel = "Direct Match", Importance = s.Importance });

        foreach (var s in baselineMatch.ImplicitlyDiscoveredSkills)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, Tier = "T2", TierLabel = "Foundation (SUBSET_OF)" });

        foreach (var s in baselineMatch.PrerequisiteMetSkills)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, OriginalName = s.OriginalName,
                Tier = "T3", TierLabel = "Prerequisite Met", BridgePath = s.BridgePath, Importance = s.Importance });

        foreach (var s in baselineMatch.BridgeableSkills)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, OriginalName = s.OriginalName,
                Tier = "T4", TierLabel = "Bridgeable (IS_SIMILAR_TO)", BridgePath = s.BridgePath, Importance = s.Importance });

        foreach (var s in baselineMatch.HardGaps)
            identifiedSkills.Add(new IdentifiedSkillDetail {
                CanonicalName = s.SkillName, OriginalName = s.OriginalName,
                Tier = "T5", TierLabel = "Hard Gap", Importance = s.Importance });

        return Ok(new
        {
            resumeId         = request.ResumeId,
            jobId            = request.JobId,
            jobTitle,
            chatGptModel     = _chatGptModel,
            identifiedSkills,
            original         = condA,
            gptGraphRag      = condB,
            chatGptBaseline  = condC,
        });
    }

    private async Task<(string TailoredFullText, string SummaryText)> RunMistralBaselinePipelineAsync(
        string rawResume, string rawJob)
    {
        var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "ChatGptTailoring.md");
        string promptTemplate;
        try { promptTemplate = await System.IO.File.ReadAllTextAsync(promptPath); }
        catch { promptTemplate = "Rewrite the following resume to better match the job description. Return plain text only.\n\nResume:\n{rawResumeText}\n\nJob Description:\n{rawJobText}"; }

        var prompt = promptTemplate
            .Replace("{rawResumeText}", rawResume)
            .Replace("{rawJobText}", rawJob);

        try
        {
            var response = await _chatClient.GetResponseAsync(prompt);
            var fullText = response?.Text?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(fullText))
                return ("", "");

            var summaryText = "";
            var lines = fullText.Split('\n');
            var summaryStart = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Equals("SUMMARY", StringComparison.OrdinalIgnoreCase))
                {
                    summaryStart = i + 1;
                    break;
                }
            }
            if (summaryStart >= 0)
            {
                var summaryLines = new List<string>();
                for (int i = summaryStart; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) break;
                    summaryLines.Add(lines[i].Trim());
                }
                summaryText = string.Join(" ", summaryLines).Trim();
            }
            else
            {
                var paraLines = new List<string>();
                bool started = false;
                foreach (var line in lines)
                {
                    if (!started && string.IsNullOrWhiteSpace(line)) continue;
                    started = true;
                    if (string.IsNullOrWhiteSpace(line)) break;
                    paraLines.Add(line.Trim());
                }
                summaryText = string.Join(" ", paraLines).Trim();
            }

            return (fullText, summaryText);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mistral baseline pipeline failed; returning empty output");
        }
        return ("", "");
    }

    //Debug endpoints
    [HttpGet("debug/graph/{skillName}")]
    public async Task<IActionResult> DebugGraphSkill(string skillName)
    {
        var neighborhood = await _graphService.GetValidNeighborhoodAsync(new[] { skillName });
        return Ok(new { skill = skillName, neighborhood });
    }

    //Shared helpers 

    private static (List<SkillMatchDetail> Matched, List<SkillMatchDetail> Missing) ComputeKeywordMatch(
        string text, List<JobSkill> jobSkills)
    {
        var matched = new List<SkillMatchDetail>();
        var missing = new List<SkillMatchDetail>();
        foreach (var skill in jobSkills)
        {
            var canonicalHit = text.Contains(skill.Name, StringComparison.OrdinalIgnoreCase);
            var originalHit  = !string.IsNullOrWhiteSpace(skill.OriginalName)
                               && text.Contains(skill.OriginalName, StringComparison.OrdinalIgnoreCase);
            if (canonicalHit || originalHit)
                matched.Add(new SkillMatchDetail
                {
                    CanonicalName = skill.Name,
                    OriginalName  = skill.OriginalName,
                    MatchedOn     = canonicalHit ? skill.Name : skill.OriginalName,
                });
            else
                missing.Add(new SkillMatchDetail
                {
                    CanonicalName = skill.Name,
                    OriginalName  = skill.OriginalName,
                    MatchedOn     = null,
                });
        }
        return (matched, missing);
    }

    private async Task<double> ComputeSummaryBridgeRate(
        string summary,
        MatchAnalysisResult baseline,
        ARIS.Shared.Models.CleanSignal.ResumeCleanSignal? resumeSignal,
        ARIS.Shared.Models.CleanSignal.JobPostingCleanSignal jobSignal)
    {
        var total = baseline.PrerequisiteMetSkills.Count + baseline.BridgeableSkills.Count;
        if (total == 0) return 0.0;

        var originalMappings = BuildOriginalMappings(resumeSignal, jobSignal);
        var summarySkills = await _matchService.ExtractCanonicalSkillsFromTailoredTextAsync(
            [], summary, originalMappings);

        var t3t4Names = new HashSet<string>(
            baseline.PrerequisiteMetSkills.Select(s => s.SkillName)
                .Concat(baseline.BridgeableSkills.Select(s => s.SkillName)),
            StringComparer.OrdinalIgnoreCase);

        var bridgeCount = summarySkills.Count(s => t3t4Names.Contains(s));
        return Math.Round((double)bridgeCount / total, 4);
    }

    private static IEnumerable<(string Original, string Canonical)> BuildOriginalMappings(
        ARIS.Shared.Models.CleanSignal.ResumeCleanSignal? resumeSignal,
        ARIS.Shared.Models.CleanSignal.JobPostingCleanSignal? jobSignal)
    {
        var mappings = new List<(string, string)>();

        if (resumeSignal != null)
        {
            foreach (var s in resumeSignal.Skills.Where(s => s.OriginalName != null))
                mappings.Add((s.OriginalName!, s.Name));
        }

        if (jobSignal != null)
        {
            foreach (var s in jobSignal.RequiredSkills.Where(s => s.OriginalName != null))
                mappings.Add((s.OriginalName!, s.Name));
        }

        return mappings;
    }

    private async Task<double> ComputeSemanticSimilarityAsync(string? text, string jobText)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(jobText))
            return 0.0;

        var textEmb = await _resumeService.GenerateEmbeddingAsync(text);
        var jobEmb  = await _resumeService.GenerateEmbeddingAsync(jobText);

        double dot = 0, normT = 0, normJ = 0;
        for (int i = 0; i < textEmb.Length; i++)
        {
            dot   += textEmb[i] * jobEmb[i];
            normT += textEmb[i] * textEmb[i];
            normJ += jobEmb[i]  * jobEmb[i];
        }
        return (normT > 0 && normJ > 0) ? Math.Round(dot / (Math.Sqrt(normT) * Math.Sqrt(normJ)), 4) : 0.0;
    }

    /// <summary>
    /// Builds the set of forbidden terms for hallucination detection from hard gap skills.
    /// Includes both the canonical name AND the job's original wording so that paraphrased
    /// forms (e.g. "Async Python" → canonical "Asynchronous Django") are also caught.
    /// </summary>
    private static HashSet<string> BuildHardGapNameSet(IEnumerable<ARIS.Shared.Models.SkillGapItem> hardGaps)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gap in hardGaps)
        {
            if (!string.IsNullOrWhiteSpace(gap.SkillName))
                set.Add(gap.SkillName);
            if (!string.IsNullOrWhiteSpace(gap.OriginalName))
                set.Add(gap.OriginalName);
        }
        return set;
    }

    private static bool ContainsWholeWord(string text, string word)
    {
        var pattern = $@"(?<![a-zA-Z0-9]){System.Text.RegularExpressions.Regex.Escape(word)}(?![a-zA-Z0-9])";
        return System.Text.RegularExpressions.Regex.IsMatch(text, pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
