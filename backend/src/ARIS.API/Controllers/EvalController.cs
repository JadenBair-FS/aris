using ARIS.API.Services;
using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using ARIS.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    public EvalController(
        MatchService matchService,
        GroundingService groundingService,
        ExtractionBenchmarkService benchmarkService,
        ArisDbContext context,
        IChatClient chatClient,
        ILogger<EvalController> logger)
    {
        _matchService = matchService;
        _groundingService = groundingService;
        _benchmarkService = benchmarkService;
        _context = context;
        _chatClient = chatClient;
        _logger = logger;
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

        // Raw text for Pipeline A and B — these baselines never see the Clean Signal
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
            // Retrieve top-20 reference skills closest to the job description embedding
            var topRefSkills = await _context.Skills
                .Where(s => s.Embedding != null && job.Embedding != null)
                .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(job.Embedding!) })
                .OrderBy(x => x.Distance)
                .Take(20)
                .ToListAsync();

            var refSkillContext = string.Join(", ", topRefSkills.Select(s => s.Name));

            // Build clean signal skill vocabulary — same structured input as Pipeline C
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

        // Grounding on canonical names — consistent with Pipeline C
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
                // Grounding computed on positive identifications only — hard gaps are job requirements
                // the candidate lacks, not claims made about the candidate's knowledge.
                // Use direct skill list validation (not text extraction) because Pipeline C skill names
                // are already canonical — ExtractSkillsFromText would find spurious sub-skill matches.
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
