using ARIS.API.Services;
using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using ARIS.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace ARIS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EvalController : ControllerBase
{
    private readonly MatchService _matchService;
    private readonly GroundingService _groundingService;
    private readonly ExtractionBenchmarkService _benchmarkService;
    private readonly ArisDbContext _context;
    private readonly IChatClient _chatClient;
    private readonly ILogger<EvalController> _logger;
    private readonly ResumeService _resumeService;
    private readonly ResumePdfService _resumePdfService;

    public EvalController(
        MatchService matchService,
        GroundingService groundingService,
        ExtractionBenchmarkService benchmarkService,
        ArisDbContext context,
        IChatClient chatClient,
        ILogger<EvalController> logger,
        ResumeService resumeService,
        ResumePdfService resumePdfService)
    {
        _matchService = matchService;
        _groundingService = groundingService;
        _benchmarkService = benchmarkService;
        _context = context;
        _chatClient = chatClient;
        _logger = logger;
        _resumeService = resumeService;
        _resumePdfService = resumePdfService;
    }

    public class GroundingRequest
    {
        public string Text { get; set; } = "";
        public Guid UserProfileId { get; set; }
    }

    /// <summary>
    /// Computes the Graph Grounding Score for arbitrary text against a user's valid skill neighborhood.
    /// Thesis RQ2 novel metric — measures how grounded LLM output is in the knowledge graph.
    /// </summary>
    [HttpPost("grounding")]
    public async Task<IActionResult> ComputeGroundingScore([FromBody] GroundingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.UserProfileId == Guid.Empty)
            return BadRequest("Text and UserProfileId are required.");

        var user = await _context.UserProfiles.FindAsync(request.UserProfileId);
        if (user?.CleanSignal == null)
            return NotFound($"User profile {request.UserProfileId} not found or has no CleanSignal.");

        var userSkills = user.CleanSignal.Skills.Select(s => s.Name).ToList();
        var result = await _groundingService.CalculateGroundingScoreAsync(request.Text, userSkills);
        return Ok(result);
    }

    public class ImplicitSkillsRequest
    {
        public Guid UserProfileId { get; set; }
        public Guid JobId { get; set; }
    }

    /// <summary>
    /// Returns per-tier skill counts for a user-job pair (thesis RQ1 metric).
    /// ImplicitDiscoveryRate = (implicit + prereqMet + bridgeable) / total job skills.
    /// </summary>
    [HttpPost("implicit-skills")]
    public async Task<IActionResult> GetImplicitSkillCounts([FromBody] ImplicitSkillsRequest request)
    {
        if (request.UserProfileId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("UserProfileId and JobId are required.");

        var analysis = await _matchService.AnalyzeMatchAsync(request.UserProfileId, request.JobId);
        if (analysis == null)
            return NotFound("Match analysis failed. Ensure both IDs are valid and have been processed.");

        var job = await _context.JobPostings.FindAsync(request.JobId);
        var totalJobSkills = job?.CleanSignal?.RequiredSkills.Count ?? 1;

        var matchCount = analysis.MatchingSkills.Count;
        var implicitCount = analysis.ImplicitlyDiscoveredSkills.Count;
        var prereqMetCount = analysis.PrerequisiteMetSkills.Count;
        var bridgeableCount = analysis.BridgeableSkills.Count;
        var hardGapCount = analysis.HardGaps.Count;

        var implicitDiscoveryRate = (double)(implicitCount + prereqMetCount + bridgeableCount) / Math.Max(totalJobSkills, 1);

        return Ok(new
        {
            userProfileId = request.UserProfileId,
            jobId = request.JobId,
            totalJobSkills,
            matchCount,
            implicitCount,
            prereqMetCount,
            bridgeableCount,
            hardGapCount,
            implicitDiscoveryRate
        });
    }

    public class PipelineCompareRequest
    {
        public Guid UserProfileId { get; set; }
        public Guid JobId { get; set; }
    }

    /// <summary>
    /// Runs all three evaluation pipelines sequentially for thesis RQ1/RQ2/RQ3 comparison.
    /// Pipeline A: LLM Direct (no retrieval), B: Vector-RAG (top-k + LLM), C: Graph-RAG / ARIS (full AnalyzeMatchAsync).
    /// </summary>
    [HttpPost("pipeline-compare")]
    public async Task<IActionResult> ComparePipelines([FromBody] PipelineCompareRequest request)
    {
        if (request.UserProfileId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("UserProfileId and JobId are required.");

        var user = await _context.UserProfiles.FindAsync(request.UserProfileId);
        var job = await _context.JobPostings.FindAsync(request.JobId);

        if (user?.CleanSignal == null)
            return NotFound($"User profile {request.UserProfileId} not found or has no CleanSignal.");
        if (job?.CleanSignal == null)
            return NotFound($"Job {request.JobId} not found or has no CleanSignal.");

        var userSkills = user.CleanSignal.Skills.Select(s => s.Name).ToList();
        var totalJobSkills = job.CleanSignal.RequiredSkills.Count;

        var rawResumeText = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(user.RawResume ?? "{}");
            rawResumeText = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : user.RawResume ?? "";
        }
        catch { rawResumeText = user.RawResume ?? ""; }
        var rawJobText = job.RawDescription ?? "";

        var pipelineA = await RunPipelineAAsync(rawResumeText, rawJobText, userSkills, totalJobSkills);
        var pipelineB = await RunPipelineBAsync(user, job, rawResumeText, rawJobText, userSkills, totalJobSkills);
        var pipelineC = await RunPipelineCAsync(request.UserProfileId, request.JobId, userSkills, totalJobSkills);

        return Ok(new { pipelineA, pipelineB, pipelineC });
    }

    /// <summary>
    /// Pipeline A: LLM Direct baseline.
    /// Inputs: raw resume text + raw job description text only. No retrieval, no structured data.
    /// Asks the LLM to classify each job-required skill into five tiers using only parametric
    /// knowledge. Simulates a naive recruiter feeding unprocessed documents to an LLM.
    /// </summary>
    private async Task<object> RunPipelineAAsync(string rawResumeText, string rawJobText, List<string> userSkills, int totalJobSkills)
    {
        var sw = Stopwatch.StartNew();
        string rawOutput;
        try
        {
            var prompt = $$"""
                You are a talent matching assistant. Classify each job-required skill into exactly one of five tiers based on the resume below.

                TIERS:
                - direct_match: candidate explicitly has this skill
                - implicit: candidate likely has this skill from adjacent knowledge, even if not stated
                - prereq: candidate has foundational knowledge to learn this quickly
                - bridgeable: candidate has adjacent skills making this reachable with effort
                - hard_gaps: candidate clearly lacks this with no reasonable path from their background

                RAW RESUME:
                {{rawResumeText}}

                JOB REQUIRED SKILLS:
                {{rawJobText}}

                Output a single JSON object with exactly these five keys. No comments. No explanation. No corrections. No additional text before or after the JSON.
                {"direct_match": [], "implicit": [], "prereq": [], "bridgeable": [], "hard_gaps": []}
                """;

            var response = await _chatClient.GetResponseAsync(prompt);
            rawOutput = response?.Text?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline A failed.");
            rawOutput = "";
        }
        sw.Stop();

        var (matchSkills, implicitSkills, prereqSkills, bridgeableSkills, gapSkills) = ParsePipelineSkillsJsonFull(rawOutput);
        var groundingResult = await _groundingService.CalculateGroundingScoreAsync(rawOutput, userSkills);
        var implicitDiscoveryRate = (double)(matchSkills.Count + implicitSkills.Count + prereqSkills.Count + bridgeableSkills.Count) / Math.Max(totalJobSkills, 1);

        return new
        {
            pipeline = "A_LLM_Direct",
            latencyMs = sw.ElapsedMilliseconds,
            matchCount = matchSkills.Count,
            implicitCount = implicitSkills.Count,
            prereqCount = prereqSkills.Count,
            bridgeableCount = bridgeableSkills.Count,
            gapCount = gapSkills.Count,
            matchingSkills = matchSkills,
            implicitSkills,
            prereqSkills,
            bridgeableSkills,
            hardGaps = gapSkills,
            implicitDiscoveryRate,
            groundingScore = groundingResult.Score,
            rawOutput
        };
    }

    /// <summary>
    /// Pipeline B: Standard Vector-RAG baseline.
    /// Inputs: Clean Signal skill vocabulary (same structured input as Pipeline C) +
    /// top-20 reference skills retrieved by cosine similarity (the "retrieval" step) +
    /// raw texts for additional context. No graph traversal.
    /// Graph traversal is the only variable between B and C, making the comparison controlled.
    /// </summary>
    private async Task<object> RunPipelineBAsync(UserProfile user, JobPosting job, string rawResumeText, string rawJobText, List<string> userSkills, int totalJobSkills)
    {
        var sw = Stopwatch.StartNew();
        string rawOutput;
        try
        {
            var topRefSkills = await _context.Skills
                .Where(s => s.Embedding != null && job.Embedding != null)
                .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(job.Embedding!) })
                .OrderBy(x => x.Distance)
                .Take(20)
                .ToListAsync();

            var refSkillContext = string.Join(", ", topRefSkills.Select(s => s.Name));

            var candidateSkillLines = user.CleanSignal!.Skills
                .Select(s => $"{s.Name} ({s.YearsOfExperience:0.#} yrs)")
                .ToList();
            var candidateSkillContext = string.Join(", ", candidateSkillLines);

            var jobSkillLines = job.CleanSignal!.RequiredSkills
                .Select(s => $"{s.Name} [{s.Importance}] ({s.YearsOfExperience:0.#} yrs required)")
                .ToList();
            var jobSkillContext = string.Join(", ", jobSkillLines);

            var prompt = $$"""
                You are a talent matching assistant. Classify each required job skill into exactly one of five tiers using the candidate profile and reference vocabulary below.

                TIERS:
                - direct_match: candidate explicitly has this skill (listed in their profile)
                - implicit: candidate likely has this skill from closely related skills, even if not listed
                - prereq: candidate has a foundational skill giving them prerequisites to learn this quickly
                - bridgeable: candidate has adjacent skills making this reachable with some effort
                - hard_gaps: candidate clearly lacks this with no reasonable path from their background

                CANDIDATE SKILLS:
                {{candidateSkillContext}}

                JOB REQUIRED SKILLS:
                {{jobSkillContext}}

                REFERENCE VOCABULARY (canonical skill names from knowledge base):
                {{refSkillContext}}

                RAW RESUME (additional context):
                {{rawResumeText}}

                RAW JOB DESCRIPTION (additional context):
                {{rawJobText}}

                Output a single JSON object with exactly these five keys. No comments. No explanation. No corrections. No additional text before or after the JSON.
                {"direct_match": [], "implicit": [], "prereq": [], "bridgeable": [], "hard_gaps": []}
                """;

            var response = await _chatClient.GetResponseAsync(prompt);
            rawOutput = response?.Text?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline B failed.");
            rawOutput = "";
        }
        sw.Stop();

        var (matchSkills, implicitSkills, prereqSkills, bridgeableSkills, gapSkills) = ParsePipelineSkillsJsonFull(rawOutput);

        var positiveSkills = matchSkills.Concat(implicitSkills).Concat(prereqSkills).Concat(bridgeableSkills).ToList();
        var groundingResult = await _groundingService.CalculateGroundingScoreFromSkillsAsync(positiveSkills, userSkills);
        var implicitDiscoveryRate = (double)(matchSkills.Count + implicitSkills.Count + prereqSkills.Count + bridgeableSkills.Count) / Math.Max(totalJobSkills, 1);

        return new
        {
            pipeline = "B_Vector_RAG",
            latencyMs = sw.ElapsedMilliseconds,
            matchCount = matchSkills.Count,
            implicitCount = implicitSkills.Count,
            prereqCount = prereqSkills.Count,
            bridgeableCount = bridgeableSkills.Count,
            gapCount = gapSkills.Count,
            matchingSkills = matchSkills,
            implicitSkills,
            prereqSkills,
            bridgeableSkills,
            hardGaps = gapSkills,
            implicitDiscoveryRate,
            groundingScore = groundingResult.Score,
            rawOutput
        };
    }

    /// <summary>
    /// Pipeline C: ARIS Hybrid GraphRAG.
    /// Grounding score is computed only on positively identified skills (matching + implicit +
    /// prereqMet + bridgeable) — consistent with how GenerateGroundedSummaryAsync works in
    /// production, where hard gaps are fed as input context to the LLM but are not part of
    /// the generated output being scored.
    /// </summary>
    private async Task<object> RunPipelineCAsync(Guid userProfileId, Guid jobId, List<string> userSkills, int totalJobSkills)
    {
        var sw = Stopwatch.StartNew();
        MatchAnalysisResult? analysis = null;
        double groundingScore = 0.0;

        try
        {
            analysis = await _matchService.AnalyzeMatchAsync(userProfileId, jobId);

            if (analysis != null)
            {
                var positiveSkills = new List<string>();
                positiveSkills.AddRange(analysis.MatchingSkills.Select(s => s.SkillName));
                positiveSkills.AddRange(analysis.ImplicitlyDiscoveredSkills);
                positiveSkills.AddRange(analysis.PrerequisiteMetSkills.Select(s => s.SkillName));
                positiveSkills.AddRange(analysis.BridgeableSkills.Select(s => s.SkillName));

                var groundingResult = await _groundingService.CalculateGroundingScoreFromSkillsAsync(positiveSkills, userSkills);
                groundingScore = groundingResult.Score;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline C failed.");
        }
        sw.Stop();

        var matchCount    = analysis?.MatchingSkills.Count ?? 0;
        var implicitCount = analysis?.ImplicitlyDiscoveredSkills.Count ?? 0;
        var prereqMet     = analysis?.PrerequisiteMetSkills.Count ?? 0;
        var bridgeable    = analysis?.BridgeableSkills.Count ?? 0;
        var hardGapCount  = analysis?.HardGaps.Count ?? 0;
        var implicitDiscoveryRate = (double)(matchCount + implicitCount + prereqMet + bridgeable) / Math.Max(totalJobSkills, 1);

        return new
        {
            pipeline = "C_Graph_RAG_ARIS",
            latencyMs = sw.ElapsedMilliseconds,
            matchCount,
            implicitCount,
            prereqMetCount = prereqMet,
            bridgeableCount = bridgeable,
            hardGapCount,
            implicitDiscoveryRate,
            groundingScore,
            vectorSimilarity = analysis?.VectorSimilarity ?? 0.0,
            matchingSkills    = analysis?.MatchingSkills.Select(s => s.SkillName).ToList() ?? [],
            implicitSkills    = analysis?.ImplicitlyDiscoveredSkills ?? [],
            prereqMetSkills   = analysis?.PrerequisiteMetSkills.Select(s => s.SkillName).ToList() ?? [],
            bridgeableSkills  = analysis?.BridgeableSkills.Select(s => s.SkillName).ToList() ?? [],
            hardGaps          = analysis?.HardGaps.Select(s => s.SkillName).ToList() ?? []
        };
    }

    /// <summary>
    /// Benchmarks LLM vs SLM extraction quality and latency (thesis latency metric).
    /// Times only the inference call per model run, excluding reference vocabulary retrieval.
    /// </summary>
    [HttpPost("extraction-benchmark")]
    public async Task<IActionResult> ExtractionBenchmark([FromBody] ExtractionBenchmarkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest("Text is required.");
        if (request.Runs < 1 || request.Runs > 10)
            return BadRequest("Runs must be between 1 and 10.");
        if (request.Models == null || request.Models.Count == 0)
            return BadRequest("At least one model must be specified.");

        var result = await _benchmarkService.RunBenchmarkAsync(request);
        return Ok(result);
    }

    public class TailorDeltaRequest
    {
        public Guid ResumeId { get; set; }
        public Guid JobId { get; set; }
    }

    /// <summary>
    /// Runs the full GraphRAG tailoring pipeline (T-C) once and returns both scoring metrics
    /// and the tailored PDF. Single LLM execution — no double-compute.
    /// Returns: baselineScore, verifiedScore, delta, articulatedSkills, hallucinations,
    ///          tierBreakdown, groundingScore, pdfBase64.
    /// </summary>
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
        var userSkills = user?.CleanSignal?.Skills.Select(s => s.Name).ToList() ?? [];

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

        var tailoredFullText = string.Join(" ", tailoredData.TailoredBullets.Select(b => b.RewrittenBullet))
            + " " + (tailoredData.ProfessionalSummary ?? "")
            + " " + string.Join(" ", tailoredData.CleanSignal?.Skills.Select(s => s.OriginalName ?? s.Name) ?? [])
            + " " + string.Join(" ", tailoredData.CleanSignal?.UngroundedSkills.Select(s => s.OriginalName ?? s.Name) ?? [])
            + " " + string.Join(" ", tailoredData.CleanSignal?.ExperienceSummary.SelectMany(e => e.Bullets) ?? []);
        var atsDebug = new {
            rawResumeWordCount = ComputeTokenSet(rawResumeText).Count,
            tailoredPdfWordCount = ComputeTokenSet(tailoredFullText).Count,
            jobWordCount = ComputeTokenSet(rawJobText).Count,
            rawResumePreview = rawResumeText.Length > 400 ? rawResumeText[..400] : rawResumeText,
            tailoredPdfPreview = tailoredFullText.Length > 400 ? tailoredFullText[..400] : tailoredFullText,
            jobPreview = rawJobText.Length > 400 ? rawJobText[..400] : rawJobText,
        };
        var atsBaseline  = ComputeJobAlignmentToken(rawResumeText, rawJobText);
        var atsTailored  = ComputeJobAlignmentToken(tailoredFullText, rawJobText);
        var atsDeltaPct  = atsBaseline > 0
            ? Math.Round((atsTailored - atsBaseline) / atsBaseline * 100.0, 2)
            : 0.0;

        var semBaseline = await ComputeSemanticSimilarityAsync(rawResumeText, rawJobText);
        var semTailored = await ComputeSemanticSimilarityAsync(tailoredFullText, rawJobText);
        var semDeltaPct = semBaseline > 0 ? Math.Round((semTailored - semBaseline) / semBaseline * 100.0, 2) : 0.0;

        var contentPres  = ComputeJobAlignmentToken(tailoredFullText, rawResumeText);

        var originalMappingsDelta = BuildOriginalMappings(tailoredData.CleanSignal, job.CleanSignal);
        var canonicalSkills = await _matchService.ExtractCanonicalSkillsFromTailoredTextAsync(
            tailoredData.TailoredBullets, tailoredData.ProfessionalSummary, originalMappingsDelta);

        var scoring = _matchService.VerifiedMatchScore(baselineMatch, canonicalSkills, job.CleanSignal);

        var groundingResult = await _groundingService.CalculateGroundingScoreFromSkillsAsync(canonicalSkills, userSkills);

        var totalJobSkills = job.CleanSignal.RequiredSkills.Count;
        var implicitDiscoveryRate = (double)(baselineMatch.ImplicitlyDiscoveredSkills.Count + 
                                            baselineMatch.PrerequisiteMetSkills.Count + 
                                            baselineMatch.BridgeableSkills.Count) / Math.Max(totalJobSkills, 1);

        sw.Stop();

        return Ok(new
        {
            resumeId = request.ResumeId,
            jobId = request.JobId,
            latencyMs = sw.ElapsedMilliseconds,
            baselineScore = scoring.BaselineScore,
            verifiedScore = scoring.VerifiedScore,
            delta = scoring.Delta,
            deltaPercent = Math.Round(scoring.Delta * 100, 2),
            articulatedSkills = scoring.ArticulatedSkills,
            hallucinations = scoring.Hallucinations,
            hallucinationCount = scoring.HallucinationCount,
            canonicalSkillsExtracted = canonicalSkills.ToList(),
            tierBreakdown = new
            {
                t1 = baselineMatch.MatchingSkills.Count,
                t2 = baselineMatch.ImplicitlyDiscoveredSkills.Count,
                t3 = baselineMatch.PrerequisiteMetSkills.Count,
                t4 = baselineMatch.BridgeableSkills.Count,
                t5 = baselineMatch.HardGaps.Count
            },
            groundingScore = groundingResult.Score,
            atsBaselineScore    = atsBaseline,
            atsTailoredScore    = atsTailored,
            atsDeltaPercent     = atsDeltaPct,
            atsSemanticBaselineScore = semBaseline,
            atsSemanticTailoredScore = semTailored,
            atsSemanticDeltaPercent  = semDeltaPct,
            implicitDiscoveryRate    = implicitDiscoveryRate,
            contentPreservation = contentPres,
            atsDebug            = atsDebug,
            pdfBase64 = Convert.ToBase64String(pdfBytes)
        });
    }

    public class TailorCompareRequest
    {
        public Guid ResumeId { get; set; }
        public Guid JobId { get; set; }
    }

    private record TailorPipelineResult(
        double BaselineScore,
        double VerifiedScore,
        double Delta,
        List<string> ArticulatedSkills,
        List<string> Hallucinations,
        int HallucinationCount,
        double GroundingScore,
        long LatencyMs,
        byte[] PdfBytes,
        double AtsBaselineScore,
        double AtsTailoredScore,
        double AtsDeltaPercent,
        double AtsSemanticBaselineScore,
        double AtsSemanticTailoredScore,
        double AtsSemanticDeltaPercent,
        double GroundedSemanticBaseline,
        double GroundedSemanticTailored,
        double ContentPreservation,
        int T5InVocabularyCount,     // T5 skills in Vector-RAG's retrieved vocabulary (0 for GraphRAG)
        double ImplicitDiscoveryRate,
        string ProfessionalSummary,
        List<TailoredBullet> TailoredBullets,
        double AtsSemanticSummaryScore
    );

    /// <summary>
    /// Runs two tailoring pipelines (T-A: Vector-RAG, T-B: GraphRAG+KG)
    /// and returns side-by-side scoring + PDF artifacts for each.
    /// Both share the same baselineMatch allowed set (computed once).
    /// </summary>
    [HttpPost("tailor-compare")]
    public async Task<IActionResult> TailorCompare([FromBody] TailorCompareRequest request)
    {
        if (request.ResumeId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("ResumeId and JobId are required.");

        var user = await _context.UserProfiles.FindAsync(request.ResumeId);
        var job = await _context.JobPostings.FindAsync(request.JobId);

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

        var userSkills = user.CleanSignal.Skills.Select(s => s.Name).ToList();

        var baselineMatch = await _matchService.AnalyzeMatchAsync(request.ResumeId, request.JobId);
        if (baselineMatch == null)
            return NotFound("Match analysis failed.");

        var pipelineA = await RunTailorPipelineAAsync(rawResumeText, rawJobText, user, baselineMatch, job.CleanSignal, userSkills, job);
        var pipelineB = await RunTailorPipelineBAsync(request.ResumeId, request.JobId, baselineMatch, job.CleanSignal, userSkills);

        static object ToResult(TailorPipelineResult r) => new
        {
            baselineScore = r.BaselineScore,
            verifiedScore = r.VerifiedScore,
            delta = r.Delta,
            deltaPercent = Math.Round(r.Delta * 100, 2),
            articulatedSkills = r.ArticulatedSkills,
            hallucinations = r.Hallucinations,
            hallucinationCount = r.HallucinationCount,
            groundingScore = r.GroundingScore,
            latencyMs = r.LatencyMs,
            atsBaselineScore    = r.AtsBaselineScore,
            atsTailoredScore    = r.AtsTailoredScore,
            atsDeltaPercent     = r.AtsDeltaPercent,
            atsSemanticBaselineScore = r.AtsSemanticBaselineScore,
            atsSemanticTailoredScore = r.AtsSemanticTailoredScore,
            atsSemanticDeltaPercent  = r.AtsSemanticDeltaPercent,
            groundedSemanticBaseline = r.GroundedSemanticBaseline,
            groundedSemanticTailored = r.GroundedSemanticTailored,
            contentPreservation = r.ContentPreservation,
            t5InVocabularyCount = r.T5InVocabularyCount,
            implicitDiscoveryRate = r.ImplicitDiscoveryRate,
            professionalSummary = r.ProfessionalSummary,
            tailoredBullets = r.TailoredBullets.Select(b => new { b.Role, b.Company, b.OriginalBullet, b.RewrittenBullet }),
            pdfBase64 = Convert.ToBase64String(r.PdfBytes)
        };

        return Ok(new
        {
            resumeId = request.ResumeId,
            jobId = request.JobId,
            pipelineA = ToResult(pipelineA),
            pipelineB = ToResult(pipelineB)
        });
    }

    private async Task<TailorPipelineResult> RunTailorPipelineAAsync(
        string rawResume, string rawJob,
        UserProfile user,
        MatchAnalysisResult baselineMatch,
        ARIS.Shared.Models.CleanSignal.JobPostingCleanSignal jobSignal,
        List<string> userSkills,
        JobPosting job)
    {
        var sw = Stopwatch.StartNew();
        var jobTitle = jobSignal.TargetRoles?.FirstOrDefault()?.Title ?? "the role";
        var tailoredBullets = new List<TailoredBullet>();

        // Vector-RAG: retrieve top-25 ref skills by cosine distance to job embedding
        var topRefSkills = await _context.Skills
            .Where(s => s.Embedding != null && job.Embedding != null)
            .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(job.Embedding!) })
            .OrderBy(x => x.Distance)
            .Take(25)
            .ToListAsync();
        var refSkillNames = topRefSkills.Select(s => s.Name).ToList();
        var refSkillContext = string.Join(", ", refSkillNames);

        // T5 contamination: how many of the top-25 retrieved skills are hard gaps
        var hardGapNameSet = new HashSet<string>(baselineMatch.HardGaps.Select(s => s.SkillName), StringComparer.OrdinalIgnoreCase);
        var t5InVocab = refSkillNames.Count(name => hardGapNameSet.Contains(name));

        var experienceEntries = user.CleanSignal!.ExperienceSummary
            .Where(e => e.Bullets.Any(b => !string.IsNullOrWhiteSpace(b)))
            .ToList();

        foreach (var exp in experienceEntries)
        {
            var bulletsText = string.Join("\n", exp.Bullets.Select((b, i) => $"{i + 1}. {b}"));
            var prompt = $$"""
                You are an expert resume writer. Rewrite the bullets below for the target job using the raw texts and reference skill vocabulary.

                FULL RESUME:
                {{rawResume}}

                FULL JOB DESCRIPTION:
                {{rawJob}}

                REFERENCE SKILL VOCABULARY (canonical skill names from knowledge base):
                {{refSkillContext}}

                EXPERIENCE ENTRY TO REWRITE:
                Role: {{exp.Role}} | Company: {{exp.Company}}
                {{bulletsText}}

                TASK: Rewrite each bullet to naturally surface skills from the reference vocabulary where they genuinely apply.
                Do not invent responsibilities.
                Use plain text only — no markdown, no asterisks, no bold, no italic, no bullet symbols, no special characters or formatting of any kind.
                Return JSON array only: [{"original": "...", "rewritten": "..."}]
                """;

            tailoredBullets.AddRange(await ParseBulletsFromLlmAsync(prompt, exp));
        }

        var summaryPrompt = $$"""
            Write a concise 3-4 sentence professional summary for this candidate targeting: {{jobTitle}}.
            Reference skill vocabulary: {{refSkillContext}}
            Use plain text only — no markdown, no asterisks, no bold, no italic, no bullet symbols, no special characters or formatting of any kind.
            RESUME: {{rawResume}}
            JOB DESCRIPTION: {{rawJob}}
            Output only the summary paragraph.
            """;
        string summary;
        try { summary = (await _chatClient.GetResponseAsync(summaryPrompt))?.Text?.Trim() ?? ""; }
        catch { summary = ""; }

        var personalInfo = ExtractPersonalInfoFromRawText(rawResume);

        var pdfBytes = _resumePdfService.GeneratePdf(personalInfo, user.CleanSignal, summary, tailoredBullets);

        var originalMappingsA = BuildOriginalMappings(user.CleanSignal, jobSignal);
        var canonicalSkills = await _matchService.ExtractCanonicalSkillsFromTailoredTextAsync(tailoredBullets, summary, originalMappingsA);
        var scoring = _matchService.VerifiedMatchScore(baselineMatch, canonicalSkills, jobSignal);
        var groundingResult = await _groundingService.CalculateGroundingScoreFromSkillsAsync(canonicalSkills, userSkills);

        var tailoredFullText = string.Join(" ", tailoredBullets.Select(b => b.RewrittenBullet))
            + " " + summary
            + " " + string.Join(" ", user.CleanSignal?.Skills.Select(s => s.OriginalName ?? s.Name) ?? [])
            + " " + string.Join(" ", user.CleanSignal?.UngroundedSkills.Select(s => s.OriginalName ?? s.Name) ?? [])
            + " " + string.Join(" ", user.CleanSignal?.ExperienceSummary.SelectMany(e => e.Bullets) ?? []);
        var atsBaseline = ComputeJobAlignmentToken(rawResume, rawJob);
        var atsTailored = ComputeJobAlignmentToken(tailoredFullText, rawJob);
        var atsDeltaPct = atsBaseline > 0 ? Math.Round((atsTailored - atsBaseline) / atsBaseline * 100.0, 2) : 0.0;

        var semBaseline = await ComputeSemanticSimilarityAsync(rawResume, rawJob);
        var semTailored = await ComputeSemanticSimilarityAsync(tailoredFullText, rawJob);
        var semSummary  = await ComputeSemanticSimilarityAsync(summary, rawJob);
        var semDeltaPct = semBaseline > 0 ? Math.Round((semTailored - semBaseline) / semBaseline * 100.0, 2) : 0.0;

        var groundedBaseline = Math.Round(semBaseline * scoring.BaselineScore, 4);
        var groundedTailored = Math.Round(semTailored * groundingResult.Score, 4);

        var contentPres = ComputeJobAlignmentToken(tailoredFullText, rawResume);

        sw.Stop();
        return new TailorPipelineResult(scoring.BaselineScore, scoring.VerifiedScore, scoring.Delta,
            scoring.ArticulatedSkills, scoring.Hallucinations, scoring.HallucinationCount,
            groundingResult.Score, sw.ElapsedMilliseconds, pdfBytes,
            atsBaseline, atsTailored, atsDeltaPct, 
            semBaseline, semTailored, semDeltaPct,
            groundedBaseline, groundedTailored,
            contentPres, t5InVocab, 0.0,
            summary, tailoredBullets, semSummary);
    }

    private async Task<TailorPipelineResult> RunTailorPipelineBAsync(
        Guid resumeId, Guid jobId,
        MatchAnalysisResult baselineMatch,
        ARIS.Shared.Models.CleanSignal.JobPostingCleanSignal jobSignal,
        List<string> userSkills)
    {
        var sw = Stopwatch.StartNew();

        var tailoredData = await _resumeService.BuildTailoredResumeDataAsync(resumeId, jobId, precomputedMatch: baselineMatch);
        if (tailoredData == null)
            return new TailorPipelineResult(0, 0, 0, [], [], 0, 0, sw.ElapsedMilliseconds, [], 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", [], 0);

        // Load raw texts for ATS computation
        var userEntity = await _context.UserProfiles.FindAsync(resumeId);
        var jobEntity  = await _context.JobPostings.FindAsync(jobId);
        string rawResume;
        try
        {
            using var doc = JsonDocument.Parse(userEntity?.RawResume ?? "{}");
            rawResume = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : userEntity?.RawResume ?? "";
        }
        catch { rawResume = userEntity?.RawResume ?? ""; }
        var rawJob = jobEntity?.RawDescription ?? "";

        var pdfBytes = _resumePdfService.GeneratePdf(
            tailoredData.PersonalInfo, tailoredData.CleanSignal,
            tailoredData.ProfessionalSummary, tailoredData.TailoredBullets);

        var originalMappingsB = BuildOriginalMappings(tailoredData.CleanSignal, jobSignal);
        var canonicalSkills = await _matchService.ExtractCanonicalSkillsFromTailoredTextAsync(
            tailoredData.TailoredBullets, tailoredData.ProfessionalSummary, originalMappingsB);

        var scoring = _matchService.VerifiedMatchScore(baselineMatch, canonicalSkills, jobSignal);
        var groundingResult = await _groundingService.CalculateGroundingScoreFromSkillsAsync(canonicalSkills, userSkills);

        var tailoredFullText = string.Join(" ", tailoredData.TailoredBullets.Select(b => b.RewrittenBullet))
            + " " + (tailoredData.ProfessionalSummary ?? "")
            + " " + string.Join(" ", tailoredData.CleanSignal?.Skills.Select(s => s.OriginalName ?? s.Name) ?? [])
            + " " + string.Join(" ", tailoredData.CleanSignal?.UngroundedSkills.Select(s => s.OriginalName ?? s.Name) ?? []);

        var atsBaseline = ComputeJobAlignmentToken(rawResume, rawJob);
        var atsTailored = ComputeJobAlignmentToken(tailoredFullText, rawJob);
        var atsDeltaPct = atsBaseline > 0 ? Math.Round((atsTailored - atsBaseline) / atsBaseline * 100.0, 2) : 0.0;

        var semBaseline = await ComputeSemanticSimilarityAsync(rawResume, rawJob);
        var semTailored = await ComputeSemanticSimilarityAsync(tailoredFullText, rawJob);
        var semSummary  = await ComputeSemanticSimilarityAsync(tailoredData.ProfessionalSummary, rawJob);
        var semDeltaPct = semBaseline > 0 ? Math.Round((semTailored - semBaseline) / semBaseline * 100.0, 2) : 0.0;

        var groundedBaseline = Math.Round(semBaseline * scoring.BaselineScore, 4);
        var groundedTailored = Math.Round(semTailored * groundingResult.Score, 4);

        var contentPres = ComputeJobAlignmentToken(tailoredFullText, rawResume);

        var totalJobSkills = jobSignal.RequiredSkills.Count;
        var implicitDiscoveryRate = (double)(baselineMatch.ImplicitlyDiscoveredSkills.Count + 
                                            baselineMatch.PrerequisiteMetSkills.Count + 
                                            baselineMatch.BridgeableSkills.Count) / Math.Max(totalJobSkills, 1);

        sw.Stop();
        return new TailorPipelineResult(scoring.BaselineScore, scoring.VerifiedScore, scoring.Delta,
            scoring.ArticulatedSkills, scoring.Hallucinations, scoring.HallucinationCount,
            groundingResult.Score, sw.ElapsedMilliseconds, pdfBytes,
            atsBaseline, atsTailored, atsDeltaPct, 
            semBaseline, semTailored, semDeltaPct,
            groundedBaseline, groundedTailored,
            contentPres, 0, implicitDiscoveryRate,
            tailoredData.ProfessionalSummary, tailoredData.TailoredBullets, semSummary);
    }

    private async Task<List<TailoredBullet>> ParseBulletsFromLlmAsync(
        string prompt,
        ARIS.Shared.Models.CleanSignal.ExperienceSummary exp,
        float? temperature = null)
    {
        var results = new List<TailoredBullet>();
        try
        {
            var llmOptions = temperature.HasValue ? new ChatOptions { Temperature = temperature.Value } : null;
            var response = await _chatClient.GetResponseAsync(prompt, llmOptions);
            var text = response?.Text?.Trim() ?? "";
            var json = Regex.Replace(text, @"```(?:json)?", "").Trim();
            var startIdx = json.IndexOf('[');
            var endIdx = json.LastIndexOf(']');
            if (startIdx >= 0 && endIdx > startIdx)
                json = json[startIdx..(endIdx + 1)];

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<List<BulletItem>>(json, options);
            if (parsed != null)
            {
                foreach (var item in parsed)
                {
                    if (!string.IsNullOrWhiteSpace(item.Original) && !string.IsNullOrWhiteSpace(item.Rewritten))
                    {
                        results.Add(new TailoredBullet
                        {
                            OriginalBullet = item.Original,
                            RewrittenBullet = item.Rewritten,
                            TargetSkill = exp.Role,
                            Role = exp.Role,
                            Company = exp.Company,
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse bullets from LLM for role {Role}", exp.Role);
            foreach (var bullet in exp.Bullets.Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                results.Add(new TailoredBullet
                {
                    OriginalBullet = bullet,
                    RewrittenBullet = bullet,
                    TargetSkill = exp.Role,
                    Role = exp.Role,
                    Company = exp.Company,
                });
            }
        }
        return results;
    }

    private record BulletItem(string Original, string Rewritten);

    /// <summary>
    /// Builds original→canonical name mappings from both clean signals so that
    /// ExtractCanonicalSkillsFromTailoredTextAsync can resolve pre-grounding names
    /// (e.g. "Node.js" → "Node.js", "React.js" → "React") that the LLM may have
    /// written into the tailored text.
    /// </summary>
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

    private static PersonalInfo ExtractPersonalInfoFromRawText(string rawResume)
    {
        var lines = rawResume.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var name = lines.FirstOrDefault()?.Trim() ?? "Candidate";
        return new PersonalInfo { Name = name };
    }

    /// <summary>
    /// Computes job coverage recall: the fraction of the job description's unique words that appear in the resume.
    /// job_coverage = |W(resume) ∩ W(job)| / |W(job)|
    /// where W() is the set of unique lowercase words stripped of punctuation.
    /// Unlike the overlap coefficient, this metric never penalizes adding non-job words to the resume,
    /// making it suitable for before/after delta measurement of resume tailoring.
    /// </summary>
    private static HashSet<string> ComputeTokenSet(string text) =>
        new HashSet<string>(
            text.ToLowerInvariant()
                .Split(new char[] { ' ', '\n', '\r', '\t', ',', '.', '!', '?', ';', ':', '"', '\'', '(', ')', '-', '/', '\\', '[', ']', '{', '}' },
                    StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

    private static double ComputeJobAlignmentToken(string resumeText, string jobText)
    {
        var resumeWords = ComputeTokenSet(resumeText);
        var jobWords = ComputeTokenSet(jobText);
        int intersection = jobWords.Count(w => resumeWords.Contains(w));
        return jobWords.Count == 0 ? 0.0 : Math.Round((double)intersection / jobWords.Count, 4);
    }

    /// <summary>
    /// Extracts all text from a PDF byte array using PdfPig.
    /// Used to build the tailored full-text for ATS comparison on an equal footing with the raw resume PDF extraction.
    /// </summary>
    private static string ExtractTextFromPdfBytes(byte[] pdfBytes)
    {
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(pdfBytes);
        foreach (var page in doc.GetPages())
        {
            sb.Append(page.Text);
            sb.Append(' ');
        }
        return sb.ToString();
    }

    private async Task<double> ComputeSemanticSimilarityAsync(string resumeText, string jobText)
    {
        if (string.IsNullOrWhiteSpace(resumeText) || string.IsNullOrWhiteSpace(jobText))
            return 0.0;

        var resumeEmb = await _resumeService.GenerateEmbeddingAsync(resumeText);
        var jobEmb = await _resumeService.GenerateEmbeddingAsync(jobText);

        double dot = 0, normR = 0, normJ = 0;
        for (int i = 0; i < resumeEmb.Length; i++)
        {
            dot += resumeEmb[i] * jobEmb[i];
            normR += resumeEmb[i] * resumeEmb[i];
            normJ += jobEmb[i] * jobEmb[i];
        }
        return (normR > 0 && normJ > 0) ? Math.Round(dot / (Math.Sqrt(normR) * Math.Sqrt(normJ)), 4) : 0.0;
    }

    /// <summary>
    /// Parses Pipeline A/B 5-tier JSON output into skill lists.
    /// Keys: direct_match, implicit, prereq, bridgeable, hard_gaps.
    /// Strips inline // comments and extracts the first {…} block.
    /// </summary>
    private static (List<string> match, List<string> implicit_, List<string> prereq, List<string> bridgeable, List<string> gaps) ParsePipelineSkillsJsonFull(string json)
    {
        var empty = (new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>());
        if (string.IsNullOrWhiteSpace(json)) return empty;
        try
        {
            var cleaned = Regex.Replace(json, @"//[^\n\r]*", "");
            var start = cleaned.IndexOf('{');
            var end   = cleaned.LastIndexOf('}');
            if (start >= 0 && end > start)
                cleaned = cleaned[start..(end + 1)];

            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            static List<string> GetList(JsonElement r, string key)
            {
                if (r.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Array)
                    return el.EnumerateArray()
                             .Where(x => x.ValueKind == JsonValueKind.String)
                             .Select(x => x.GetString()!)
                             .ToList();
                return [];
            }

            return (
                GetList(root, "direct_match"),
                GetList(root, "implicit"),
                GetList(root, "prereq"),
                GetList(root, "bridgeable"),
                GetList(root, "hard_gaps")
            );
        }
        catch
        {
            return empty;
        }
    }
}
