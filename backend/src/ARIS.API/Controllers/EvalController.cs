using ARIS.API.Services;
using ARIS.Shared.Data;
using ARIS.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    // ── Tailor Delta (single ARIS pipeline, supplemental) ───────────────────────

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

    // ── Three-Way Compare (Original vs ARIS GraphRAG vs ChatGPT) ────────────────

    public class ThreeWayCompareRequest
    {
        public Guid ResumeId { get; set; }
        public Guid JobId { get; set; }
    }

    public class ThreeWayResult
    {
        public double SemanticSimilarityScore { get; set; }
        public double KeywordMatchScore { get; set; }
        public double AtsScore { get; set; }
        public int MatchingTermsCount { get; set; }
        public int MissingTermsCount { get; set; }
        public List<string> MatchingTerms { get; set; } = [];
        public List<string> MissingTerms { get; set; } = [];
        public List<string> NewTermsAdded { get; set; } = [];
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

        var jobTitle      = job.CleanSignal.TargetRoles?.FirstOrDefault()?.Title ?? "the role";
        var jobSkillNames = job.CleanSignal.RequiredSkills.Select(s => s.Name).ToList();

        var baselineMatch = await _matchService.AnalyzeMatchAsync(request.ResumeId, request.JobId);
        if (baselineMatch == null)
            return NotFound("Match analysis failed. Ensure both IDs are valid and fully processed.");

        var swA = Stopwatch.StartNew();
        var origKeywords = ComputeKeywordMatch(rawResumeText, jobSkillNames);
        var origSemScore = await ComputeSemanticSimilarityAsync(rawResumeText, rawJobText);
        var origKwScore  = jobSkillNames.Count > 0
            ? Math.Round((double)origKeywords.Matched.Count / jobSkillNames.Count, 4) : 0.0;
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

            var bulletsText = string.Join("\n", tailoredData.TailoredBullets
                .Select(b => b.RewrittenBullet)
                .Where(b => !string.IsNullOrWhiteSpace(b)));
            var arisFullText = string.IsNullOrWhiteSpace(tailoredData.ProfessionalSummary)
                ? bulletsText
                : $"{tailoredData.ProfessionalSummary}\n{bulletsText}";

            var originalMappings = BuildOriginalMappings(tailoredData.CleanSignal, job.CleanSignal);
            var canonicalSkills = await _matchService.ExtractCanonicalSkillsFromTailoredTextAsync(
                tailoredData.TailoredBullets, tailoredData.ProfessionalSummary ?? "", originalMappings);
            var scoring = _matchService.VerifiedMatchScore(baselineMatch, canonicalSkills, job.CleanSignal);

            var arisKeywords = ComputeKeywordMatch(arisFullText, jobSkillNames);
            var arisSemScore = await ComputeSemanticSimilarityAsync(arisFullText, rawJobText);
            var arisKwScore  = jobSkillNames.Count > 0
                ? Math.Round((double)arisKeywords.Matched.Count / jobSkillNames.Count, 4) : 0.0;
            var arisNewTerms = arisKeywords.Matched
                .Except(origKeywords.Matched, StringComparer.OrdinalIgnoreCase)
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
                HallucinationCount      = scoring.HallucinationCount,
                SummaryText             = tailoredData.ProfessionalSummary ?? "",
                TailoredFullText        = arisFullText,
                LatencyMs               = swB.ElapsedMilliseconds,
                PdfBase64               = Convert.ToBase64String(pdfBytes),
            };
        }

        var swC = Stopwatch.StartNew();
        var (gptFullText, gptSummary) = await RunChatGptPipelineAsync(rawResumeText, rawJobText);
        var gptKeywords = ComputeKeywordMatch(gptFullText, jobSkillNames);
        var gptSemScore = await ComputeSemanticSimilarityAsync(gptFullText, rawJobText);
        var gptKwScore  = jobSkillNames.Count > 0
            ? Math.Round((double)gptKeywords.Matched.Count / jobSkillNames.Count, 4) : 0.0;
        var gptNewTerms = gptKeywords.Matched
            .Except(origKeywords.Matched, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hardGapNames = new HashSet<string>(
            baselineMatch.HardGaps.Select(s => s.SkillName), StringComparer.OrdinalIgnoreCase);
        var gptHallucinationCount = hardGapNames.Count(name =>
            gptFullText.Contains(name, StringComparison.OrdinalIgnoreCase));
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

        return Ok(new
        {
            resumeId        = request.ResumeId,
            jobId           = request.JobId,
            jobTitle,
            chatGptModel    = _chatGptModel,
            original        = condA,
            arisGraphRag    = condB,
            chatGptBaseline = condC,
        });
    }

    private async Task<(string TailoredFullText, string SummaryText)> RunChatGptPipelineAsync(
        string rawResume, string rawJob)
    {
        if (_openAiClient == null)
            return ("ChatGPT baseline unavailable: OPENAI_API_KEY is not configured.", "");

        var prompt = $$"""
            You are helping a job seeker improve their resume for a specific job.

            Resume:
            {{rawResume}}

            Job Description:
            {{rawJob}}

            Please rewrite the resume to better highlight this candidate's fit for this role:
            1. For each work experience entry, rewrite the bullet points to emphasize relevant skills and achievements.
            2. Write a new 3-5 sentence professional summary at the top.

            Only use information present in the original resume — do not add skills or experiences the candidate has not demonstrated.

            Return your response in this JSON format:
            {
              "summary": "...",
              "experience": [
                { "company": "...", "title": "...", "bullets": ["...", "..."] }
              ]
            }
            """;

        try
        {
            var response = await _openAiClient.GetResponseAsync(prompt);
            var text = response?.Text?.Trim() ?? "";
            var json = Regex.Replace(text, @"```(?:json)?", "").Trim();
            var startIdx = json.IndexOf('{');
            var endIdx   = json.LastIndexOf('}');
            if (startIdx >= 0 && endIdx > startIdx)
                json = json[startIdx..(endIdx + 1)];

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed  = JsonSerializer.Deserialize<GptResumeResponse>(json, options);
            if (parsed != null)
            {
                var bulletsText = string.Join("\n",
                    (parsed.Experience ?? [])
                        .SelectMany(e => e.Bullets ?? [])
                        .Where(b => !string.IsNullOrWhiteSpace(b)));
                var summary  = parsed.Summary?.Trim() ?? "";
                var fullText = string.IsNullOrWhiteSpace(summary) ? bulletsText : $"{summary}\n{bulletsText}";
                return (fullText.Trim(), summary);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChatGPT pipeline failed; returning empty output");
        }
        return ("", "");
    }

    private record GptResumeResponse(string? Summary, List<GptExperienceEntry>? Experience);
    private record GptExperienceEntry(string? Company, string? Title, List<string>? Bullets);

    // ── Debug endpoints ──────────────────────────────────────────────────────────

    [HttpGet("debug/graph/{skillName}")]
    public async Task<IActionResult> DebugGraphSkill(string skillName)
    {
        var neighborhood = await _graphService.GetValidNeighborhoodAsync(new[] { skillName });
        return Ok(new { skill = skillName, neighborhood });
    }

    // ── Shared helpers ───────────────────────────────────────────────────────────

    private static (List<string> Matched, List<string> Missing) ComputeKeywordMatch(
        string text, List<string> jobSkills)
    {
        var matched = new List<string>();
        var missing = new List<string>();
        foreach (var skill in jobSkills)
        {
            if (text.Contains(skill, StringComparison.OrdinalIgnoreCase))
                matched.Add(skill);
            else
                missing.Add(skill);
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
}
