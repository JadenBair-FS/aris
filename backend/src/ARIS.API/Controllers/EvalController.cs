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

namespace ARIS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EvalController : ControllerBase
{
    private readonly MatchService _matchService;
    private readonly GroundingService _groundingService;
    private readonly ArisDbContext _context;
    private readonly IChatClient _chatClient;
    private readonly ILogger<EvalController> _logger;

    public EvalController(
        MatchService matchService,
        GroundingService groundingService,
        ArisDbContext context,
        IChatClient chatClient,
        ILogger<EvalController> logger)
    {
        _matchService = matchService;
        _groundingService = groundingService;
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
    /// D2: Computes the Graph Grounding Score for arbitrary text against a user's valid skill neighborhood.
    /// This is the thesis RQ2 novel metric — measures how grounded LLM output is in the knowledge graph.
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
    /// D3: Returns per-tier skill counts for a user-job pair (thesis RQ1 metric).
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
    /// D4: Runs all three evaluation pipelines sequentially for thesis RQ1/RQ2/RQ3 comparison.
    /// Pipeline A: LLM Direct (no retrieval)
    /// Pipeline B: Vector-RAG (top-k similarity + LLM synthesis)
    /// Pipeline C: Graph-RAG / ARIS (full AnalyzeMatchAsync)
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
        var userJson = JsonSerializer.Serialize(user.CleanSignal);
        var jobJson = JsonSerializer.Serialize(job.CleanSignal);

        // Pipeline A — LLM Direct: feed CleanSignals directly, no retrieval.
        var pipelineA = await RunPipelineAAsync(userJson, jobJson, userSkills);

        // Pipeline B — Vector-RAG: top-k skill similarity + LLM synthesis.
        var pipelineB = await RunPipelineBAsync(user, job, userSkills);

        // Pipeline C — Graph-RAG / ARIS: full AnalyzeMatchAsync.
        var pipelineC = await RunPipelineCAsync(request.UserProfileId, request.JobId, userSkills);

        return Ok(new { pipelineA, pipelineB, pipelineC });
    }

    private async Task<object> RunPipelineAAsync(string userJson, string jobJson, List<string> userSkills)
    {
        var sw = Stopwatch.StartNew();
        string rawOutput;
        try
        {
            var prompt = $$"""
                You are a talent matching system. Compare the candidate profile and job posting below.
                Identify:
                1. Skills the candidate already has that match the job ("matching")
                2. Skills the job requires that the candidate lacks ("gaps")

                CANDIDATE PROFILE (JSON): {{userJson}}

                JOB POSTING (JSON): {{jobJson}}

                Respond with JSON only: {"matching": ["skill1"], "gaps": ["skill1"]}
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

        var (matching, gaps) = ParsePipelineSkillsJson(rawOutput);
        var groundingResult = await _groundingService.CalculateGroundingScoreAsync(rawOutput, userSkills);

        return new
        {
            pipeline = "A_LLM_Direct",
            latencyMs = sw.ElapsedMilliseconds,
            matchCount = matching,
            gapCount = gaps,
            totalSkillsIdentified = matching + gaps,
            groundingScore = groundingResult.Score,
            rawOutput
        };
    }

    private async Task<object> RunPipelineBAsync(UserProfile user, JobPosting job, List<string> userSkills)
    {
        var sw = Stopwatch.StartNew();
        string rawOutput;
        try
        {
            // Top-k vector search: find skills most similar to the user's embedding.
            var topSkillNames = await _context.Skills
                    .Where(s => s.Embedding != null)
                    .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(user.Embedding!) })
                    .OrderBy(x => x.Distance)
                    .Take(20)
                    .ToListAsync();

            var ragContext = string.Join(", ", topSkillNames.Select(s => s.Name));
            var jobSkills = string.Join(", ", job.CleanSignal!.RequiredSkills.Select(s => s.Name));
            var candidateSkills = string.Join(", ", user.CleanSignal!.Skills.Select(s => s.Name));

            var prompt = $$"""
                You are a talent matching system. Using the retrieved context below, identify skill matches and gaps.

                RETRIEVED RELEVANT SKILLS (top-k vector search): {{ragContext}}

                JOB REQUIRED SKILLS: {{jobSkills}}
                CANDIDATE SKILLS: {{candidateSkills}}

                Respond with JSON only: {"matching": ["skill1"], "gaps": ["skill1"]}
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

        var (matching, gaps) = ParsePipelineSkillsJson(rawOutput);
        var groundingResult = await _groundingService.CalculateGroundingScoreAsync(rawOutput, userSkills);

        return new
        {
            pipeline = "B_Vector_RAG",
            latencyMs = sw.ElapsedMilliseconds,
            matchCount = matching,
            gapCount = gaps,
            totalSkillsIdentified = matching + gaps,
            groundingScore = groundingResult.Score,
            rawOutput
        };
    }

    private async Task<object> RunPipelineCAsync(Guid userProfileId, Guid jobId, List<string> userSkills)
    {
        var sw = Stopwatch.StartNew();
        MatchAnalysisResult? analysis = null;
        double groundingScore = 0.0;

        try
        {
            analysis = await _matchService.AnalyzeMatchAsync(userProfileId, jobId);

            if (analysis != null)
            {
                var summaryText = new StringBuilder();
                summaryText.AppendLine($"Matching: {string.Join(", ", analysis.MatchingSkills)}");
                summaryText.AppendLine($"Implicit: {string.Join(", ", analysis.ImplicitlyDiscoveredSkills)}");
                summaryText.AppendLine($"Bridgeable: {string.Join(", ", analysis.BridgeableSkills.Select(s => s.SkillName))}");
                summaryText.AppendLine($"Hard Gaps: {string.Join(", ", analysis.HardGaps.Select(s => s.SkillName))}");

                var groundingResult = await _groundingService.CalculateGroundingScoreAsync(summaryText.ToString(), userSkills);
                groundingScore = groundingResult.Score;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline C failed.");
        }
        sw.Stop();

        return new
        {
            pipeline = "C_Graph_RAG_ARIS",
            latencyMs = sw.ElapsedMilliseconds,
            matchCount = analysis?.MatchingSkills.Count ?? 0,
            implicitCount = analysis?.ImplicitlyDiscoveredSkills.Count ?? 0,
            prereqMetCount = analysis?.PrerequisiteMetSkills.Count ?? 0,
            bridgeableCount = analysis?.BridgeableSkills.Count ?? 0,
            hardGapCount = analysis?.HardGaps.Count ?? 0,
            totalSkillsIdentified = (analysis?.MatchingSkills.Count ?? 0)
                                  + (analysis?.ImplicitlyDiscoveredSkills.Count ?? 0)
                                  + (analysis?.PrerequisiteMetSkills.Count ?? 0)
                                  + (analysis?.BridgeableSkills.Count ?? 0)
                                  + (analysis?.HardGaps.Count ?? 0),
            groundingScore,
            vectorSimilarity = analysis?.VectorSimilarity ?? 0.0
        };
    }

    /// <summary>
    /// Parses Pipeline A/B JSON output to extract skill counts for comparison.
    /// Handles partial/malformed JSON gracefully.
    /// </summary>
    private static (int matching, int gaps) ParsePipelineSkillsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (0, 0);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int matching = 0, gaps = 0;

            if (root.TryGetProperty("matching", out var matchEl) && matchEl.ValueKind == JsonValueKind.Array)
                matching = matchEl.GetArrayLength();

            if (root.TryGetProperty("gaps", out var gapsEl) && gapsEl.ValueKind == JsonValueKind.Array)
                gaps = gapsEl.GetArrayLength();

            return (matching, gaps);
        }
        catch
        {
            return (0, 0);
        }
    }
}
