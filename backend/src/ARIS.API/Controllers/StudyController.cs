using ARIS.API.Services;
using ARIS.Shared.Data;
using ARIS.Shared.Models;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Pgvector.EntityFrameworkCore;
using System.Text;

namespace ARIS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public class StudyController : ControllerBase
{
    private readonly MatchService _matchService;
    private readonly JobService _jobService;
    private readonly ResumeService _resumeService;
    private readonly GraphService _graphService;
    private readonly ArisDbContext _context;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IChatClient _chatClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<StudyController> _logger;

    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(15);

    public StudyController(
        MatchService matchService,
        JobService jobService,
        ResumeService resumeService,
        GraphService graphService,
        ArisDbContext context,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IChatClient chatClient,
        IMemoryCache cache,
        ILogger<StudyController> logger)
    {
        _matchService = matchService;
        _jobService = jobService;
        _resumeService = resumeService;
        _graphService = graphService;
        _context = context;
        _embeddingGenerator = embeddingGenerator;
        _chatClient = chatClient;
        _cache = cache;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Step 1: extract + cache resume signal
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts a Clean Signal from raw resume text and caches the result for 15 minutes.
    /// Returns a session key to be passed to subsequent wizard steps.
    /// </summary>
    [HttpPost("prepare-resume")]
    public async Task<IActionResult> PrepareResume([FromBody] PrepareTextRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest("Text is required.");

        _logger.LogInformation("StudyController.PrepareResume: extracting resume signal.");

        var (signal, embedding) = await _resumeService.QuickExtractResumeSignalAsync(request.Text);

        if (signal == null || embedding == null)
            return BadRequest("Resume signal extraction failed. Please check your input and try again.");

        var key = Guid.NewGuid().ToString("N");
        _cache.Set(
            $"study:resume:{key}",
            (signal, embedding),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = SessionTtl });

        _logger.LogInformation("StudyController.PrepareResume: cached session key {Key}.", key);
        return Ok(new PrepareSessionResponse { SessionKey = key });
    }

    // -------------------------------------------------------------------------
    // Step 2: extract + cache job signal
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts and grounds a Clean Signal from raw job description text and caches the result
    /// for 15 minutes. Returns a session key to be passed to subsequent wizard steps.
    /// </summary>
    [HttpPost("prepare-job")]
    public async Task<IActionResult> PrepareJob([FromBody] PrepareTextRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest("Text is required.");

        _logger.LogInformation("StudyController.PrepareJob: extracting job signal.");

        var (signal, embedding) = await _jobService.QuickExtractAndGroundAsync(request.Text);

        if (signal == null || embedding == null)
            return BadRequest("Job signal extraction failed. Please check your input and try again.");

        var key = Guid.NewGuid().ToString("N");
        _cache.Set(
            $"study:job:{key}",
            (signal, embedding),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = SessionTtl });

        _logger.LogInformation("StudyController.PrepareJob: cached session key {Key}.", key);
        return Ok(new PrepareSessionResponse { SessionKey = key });
    }

    // -------------------------------------------------------------------------
    // Step 3: tier analysis + two parallel LLM calls using cached signals
    // -------------------------------------------------------------------------

    /// <summary>
    /// Uses cached resume and job signals to run tier analysis and generate side-by-side
    /// Standard AI (vector RAG) and GraphRAG responses. Nothing is written to the database.
    /// </summary>
    [HttpPost("generate-comparison")]
    public async Task<IActionResult> GenerateComparison([FromBody] GenerateComparisonRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ResumeKey))
            return BadRequest("ResumeKey is required.");
        if (string.IsNullOrWhiteSpace(request.JobKey))
            return BadRequest("JobKey is required.");
        if (string.IsNullOrWhiteSpace(request.ResumeText))
            return BadRequest("ResumeText is required.");
        if (string.IsNullOrWhiteSpace(request.JobDescriptionText))
            return BadRequest("JobDescriptionText is required.");

        if (!_cache.TryGetValue(
                $"study:resume:{request.ResumeKey}",
                out (ResumeCleanSignal Signal, Pgvector.Vector Embedding) resumeEntry))
        {
            return BadRequest(new { error = "Resume session expired. Please restart from step 1." });
        }

        if (!_cache.TryGetValue(
                $"study:job:{request.JobKey}",
                out (JobPostingCleanSignal Signal, Pgvector.Vector Embedding) jobEntry))
        {
            return BadRequest(new { error = "Job session expired. Please restart from step 2." });
        }

        _logger.LogInformation("StudyController.GenerateComparison: running tier analysis + parallel LLM calls.");

        var ragTask = GenerateRagResponseAsync(request.ResumeText, request.JobDescriptionText);
        var graphRagTask = GenerateGraphRagResponseFromSignalsAsync(
            resumeEntry.Signal, resumeEntry.Embedding,
            jobEntry.Signal, jobEntry.Embedding,
            request.ResumeText, request.JobDescriptionText);

        await Task.WhenAll(ragTask, graphRagTask);

        var (ragResponse, _) = ragTask.Result;
        var (graphRagResponse, tierSummary) = graphRagTask.Result;

        return Ok(new StudyCompareResponse
        {
            RagResponse      = ragResponse,
            GraphRagResponse = graphRagResponse,
            TierSummary      = tierSummary
        });
    }

    // -------------------------------------------------------------------------
    // Original compare endpoint (unchanged for backwards compatibility)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generates two side-by-side LLM analyses for a user study:
    /// one from Standard AI (vector RAG) and one from GraphRAG (tier-aware knowledge graph analysis).
    /// Nothing is written to the database — all processing is ephemeral.
    /// </summary>
    [HttpPost("compare")]
    public async Task<IActionResult> Compare([FromBody] StudyCompareRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ResumeText))
            return BadRequest("ResumeText is required.");
        if (string.IsNullOrWhiteSpace(request.JobDescriptionText))
            return BadRequest("JobDescriptionText is required.");

        _logger.LogInformation("StudyController.Compare: starting parallel RAG + GraphRAG pipeline.");

        // Run both paths concurrently. Each path is wrapped in try/catch so a failure in one
        // does not block the other from completing.
        var ragTask = GenerateRagResponseAsync(request.ResumeText, request.JobDescriptionText);
        var graphRagTask = GenerateGraphRagResponseAsync(request.ResumeText, request.JobDescriptionText);

        await Task.WhenAll(ragTask, graphRagTask);

        var (ragResponse, _) = ragTask.Result;
        var (graphRagResponse, tierSummary) = graphRagTask.Result;

        return Ok(new StudyCompareResponse
        {
            RagResponse      = ragResponse,
            GraphRagResponse = graphRagResponse,
            TierSummary      = tierSummary
        });
    }

    // -------------------------------------------------------------------------
    // Tailor endpoint — optionally uses cached signals when keys are supplied
    // -------------------------------------------------------------------------

    /// <summary>
    /// Produces a tailored resume (summary + experience bullets) using the ARIS knowledge graph.
    /// When resumeKey and jobKey are provided and found in cache, signal extraction is skipped.
    /// Otherwise falls back to re-extracting from the supplied texts.
    /// Nothing is written to the database.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("tailor")]
    public async Task<IActionResult> Tailor([FromBody] StudyTailorRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ResumeText))
            return BadRequest("ResumeText is required.");
        if (string.IsNullOrWhiteSpace(request.JobDescriptionText))
            return BadRequest("JobDescriptionText is required.");

        ResumeCleanSignal? resumeSignal;
        Pgvector.Vector? resumeEmbedding;
        JobPostingCleanSignal? jobSignal;
        Pgvector.Vector? jobEmbedding;

        // Use cached signals if both keys are provided and still live
        bool resumeFromCache = !string.IsNullOrWhiteSpace(request.ResumeKey)
            && _cache.TryGetValue(
                $"study:resume:{request.ResumeKey}",
                out (ResumeCleanSignal Signal, Pgvector.Vector Embedding) cachedResume);

        bool jobFromCache = !string.IsNullOrWhiteSpace(request.JobKey)
            && _cache.TryGetValue(
                $"study:job:{request.JobKey}",
                out (JobPostingCleanSignal Signal, Pgvector.Vector Embedding) cachedJob);

        if (resumeFromCache && jobFromCache)
        {
            _logger.LogInformation("StudyController.Tailor: using cached signals (resumeKey={R}, jobKey={J}).",
                request.ResumeKey, request.JobKey);

            // Re-fetch from cache into named variables (the out-vars above are scoped to the if conditions)
            _cache.TryGetValue(
                $"study:resume:{request.ResumeKey}",
                out (ResumeCleanSignal Signal, Pgvector.Vector Embedding) resumeTuple);
            _cache.TryGetValue(
                $"study:job:{request.JobKey}",
                out (JobPostingCleanSignal Signal, Pgvector.Vector Embedding) jobTuple);

            resumeSignal     = resumeTuple.Signal;
            resumeEmbedding  = resumeTuple.Embedding;
            jobSignal        = jobTuple.Signal;
            jobEmbedding     = jobTuple.Embedding;
        }
        else
        {
            _logger.LogInformation("StudyController.Tailor: extracting signals from text (no valid cache keys).");

            var resumeTask = _resumeService.QuickExtractResumeSignalAsync(request.ResumeText);
            var jobTask    = _jobService.QuickExtractAndGroundAsync(request.JobDescriptionText);

            await Task.WhenAll(resumeTask, jobTask);

            (resumeSignal, resumeEmbedding) = resumeTask.Result;
            (jobSignal, jobEmbedding)        = jobTask.Result;
        }

        if (resumeSignal == null || resumeEmbedding == null)
            return StatusCode(500, "Resume signal extraction failed.");
        if (jobSignal == null || jobEmbedding == null)
            return StatusCode(500, "Job signal extraction failed.");

        var matchResult = await _matchService.AnalyzeMatchFromSignalsAsync(
            resumeSignal, resumeEmbedding, jobSignal, jobEmbedding);

        if (matchResult == null)
            return StatusCode(500, "Tier classification failed.");

        var graphContext = _graphService.BuildTailoringGraphContext(matchResult, resumeSignal.Skills);

        var prompt = new StringBuilder();
        prompt.AppendLine("You are a professional resume writer. Using the skill analysis below, rewrite the candidate's work experience bullets and summary to better align with the target role. Preserve all real experience — do not fabricate or invent skills the candidate does not have. Focus on transferable skills and adjacent experience. Return only the rewritten resume sections (summary + experience bullets), formatted as plain text.");
        prompt.AppendLine();
        prompt.AppendLine("Graph context:");
        prompt.AppendLine(graphContext);
        prompt.AppendLine();
        prompt.AppendLine("Original resume text:");
        prompt.AppendLine(request.ResumeText);
        prompt.AppendLine();
        prompt.AppendLine("Job description:");
        prompt.AppendLine(request.JobDescriptionText);

        _logger.LogInformation("StudyController.Tailor: calling LLM for tailoring.");
        var response = await _chatClient.GetResponseAsync(prompt.ToString());
        var tailoredText = response.Text?.Trim() ?? "Tailored resume could not be generated.";

        return Ok(new StudyTailorResponse { TailoredText = tailoredText });
    }

    // =========================================================================
    // Private helpers
    // =========================================================================

    // RAG path: embed resume text, find top-20 skills from ref_skills, call Mistral.
    private async Task<(string Response, object Unused)> GenerateRagResponseAsync(
        string resumeText, string jobDescriptionText)
    {
        try
        {
            // Embed the combined resume text to query the reference skill dictionary
            var combinedText = resumeText.Length > 2000 ? resumeText[..2000] : resumeText;
            var embeddings = await _embeddingGenerator.GenerateAsync([combinedText]);
            var queryVector = new Pgvector.Vector(embeddings[0].Vector);

            // Top-20 skills from ref_skills by cosine similarity
            var top20Skills = await _context.Skills
                .Where(s => s.Embedding != null)
                .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(queryVector) })
                .OrderBy(x => x.Distance)
                .Take(20)
                .ToListAsync();

            var skillsList = string.Join(", ", top20Skills.Select(s => s.Name));

            var prompt = new StringBuilder();
            prompt.AppendLine($"Given this resume:\n{resumeText}");
            prompt.AppendLine();
            prompt.AppendLine($"And this job description:\n{jobDescriptionText}");
            prompt.AppendLine();
            prompt.AppendLine($"Top skills from our database that seem relevant: {skillsList}");
            prompt.AppendLine();
            prompt.AppendLine("Provide a brief analysis of how well this candidate fits the role and what gaps they may have.");

            var response = await _chatClient.GetResponseAsync(prompt.ToString());
            var text = response.Text?.Trim() ?? "Analysis could not be generated.";
            _logger.LogInformation("StudyController: RAG response generated ({Length} chars).", text.Length);
            return (text, new object());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StudyController: RAG path failed.");
            return ("Standard AI analysis is temporarily unavailable.", new object());
        }
    }

    // GraphRAG path (signal extraction variant): extract + ground both signals, run tier analysis, call Mistral.
    private async Task<(string Response, StudyTierSummary TierSummary)> GenerateGraphRagResponseAsync(
        string resumeText, string jobDescriptionText)
    {
        var emptySummary = new StudyTierSummary();

        try
        {
            // Extract and ground both signals in parallel — neither writes to the DB
            var resumeTask = _resumeService.QuickExtractResumeSignalAsync(resumeText);
            var jobTask    = _jobService.QuickExtractAndGroundAsync(jobDescriptionText);

            await Task.WhenAll(resumeTask, jobTask);

            var (resumeSignal, resumeEmbedding) = resumeTask.Result;
            var (jobSignal, jobEmbedding)        = jobTask.Result;

            if (resumeSignal == null || resumeEmbedding == null)
            {
                _logger.LogWarning("StudyController: Resume signal extraction failed for GraphRAG path.");
                return ("GraphRAG analysis is temporarily unavailable — resume signal could not be extracted.", emptySummary);
            }

            if (jobSignal == null || jobEmbedding == null)
            {
                _logger.LogWarning("StudyController: Job signal extraction failed for GraphRAG path.");
                return ("GraphRAG analysis is temporarily unavailable — job signal could not be extracted.", emptySummary);
            }

            return await BuildGraphRagResponseAsync(
                resumeSignal, resumeEmbedding, jobSignal, jobEmbedding,
                resumeText, jobDescriptionText, emptySummary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StudyController: GraphRAG path failed.");
            return ("GraphRAG analysis is temporarily unavailable.", emptySummary);
        }
    }

    // GraphRAG path (pre-extracted signals variant): skip extraction, run tier analysis, call Mistral.
    private async Task<(string Response, StudyTierSummary TierSummary)> GenerateGraphRagResponseFromSignalsAsync(
        ResumeCleanSignal resumeSignal, Pgvector.Vector resumeEmbedding,
        JobPostingCleanSignal jobSignal, Pgvector.Vector jobEmbedding,
        string resumeText, string jobDescriptionText)
    {
        var emptySummary = new StudyTierSummary();

        try
        {
            return await BuildGraphRagResponseAsync(
                resumeSignal, resumeEmbedding, jobSignal, jobEmbedding,
                resumeText, jobDescriptionText, emptySummary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StudyController: GraphRAG path (pre-extracted) failed.");
            return ("GraphRAG analysis is temporarily unavailable.", emptySummary);
        }
    }

    // Shared core logic: tier analysis + prompt building + LLM call.
    private async Task<(string Response, StudyTierSummary TierSummary)> BuildGraphRagResponseAsync(
        ResumeCleanSignal resumeSignal, Pgvector.Vector resumeEmbedding,
        JobPostingCleanSignal jobSignal, Pgvector.Vector jobEmbedding,
        string resumeText, string jobDescriptionText,
        StudyTierSummary emptySummary)
    {
        // Run full tier classification against the knowledge graph (no DB writes)
        var matchResult = await _matchService.AnalyzeMatchFromSignalsAsync(
            resumeSignal, resumeEmbedding, jobSignal, jobEmbedding);

        if (matchResult == null)
        {
            _logger.LogWarning("StudyController: AnalyzeMatchFromSignalsAsync returned null.");
            return ("GraphRAG analysis is temporarily unavailable — tier classification failed.", emptySummary);
        }

        var tierSummary = new StudyTierSummary
        {
            Tier1Count       = matchResult.MatchingSkills.Count,
            Tier2Count       = matchResult.ImplicitlyDiscoveredSkills.Count,
            Tier3Count       = matchResult.PrerequisiteMetSkills.Count,
            Tier4Count       = matchResult.BridgeableSkills.Count,
            VectorSimilarity = Math.Round(matchResult.VectorSimilarity, 4)
        };

        // Build the enriched GraphRAG prompt
        var tier1Skills = matchResult.MatchingSkills.Count > 0
            ? string.Join(", ", matchResult.MatchingSkills.Select(s => s.SkillName))
            : "None identified";
        var tier2Skills = matchResult.ImplicitlyDiscoveredSkills.Count > 0
            ? string.Join(", ", matchResult.ImplicitlyDiscoveredSkills)
            : "None identified";
        var tier3Skills = matchResult.PrerequisiteMetSkills.Count > 0
            ? string.Join(", ", matchResult.PrerequisiteMetSkills.Select(s => s.SkillName))
            : "None identified";
        var tier4Skills = matchResult.BridgeableSkills.Count > 0
            ? string.Join(", ", matchResult.BridgeableSkills.Select(s => s.SkillName))
            : "None identified";
        var hardGapSkills = matchResult.HardGaps.Count > 0
            ? string.Join(", ", matchResult.HardGaps.Select(s => s.SkillName))
            : "None identified";

        var prompt = new StringBuilder();
        prompt.AppendLine($"Given this resume:\n{resumeText}");
        prompt.AppendLine();
        prompt.AppendLine($"And this job description:\n{jobDescriptionText}");
        prompt.AppendLine();
        prompt.AppendLine("Structured skill analysis from the ARIS knowledge graph:");
        prompt.AppendLine($"- Direct matches (Tier 1 — candidate clearly has these): {tier1Skills}");
        prompt.AppendLine($"- Transferable skills (Tier 2 — implied by candidate background): {tier2Skills}");
        prompt.AppendLine($"- Prerequisite skills met (Tier 3 — candidate has the foundation): {tier3Skills}");
        prompt.AppendLine($"- Bridgeable skills (Tier 4 — reachable via knowledge-graph path): {tier4Skills}");
        prompt.AppendLine($"- Skill gaps (Tier 5 — true gaps with no graph path): {hardGapSkills}");
        prompt.AppendLine();
        prompt.AppendLine("Using the above tier breakdown as context, provide a detailed analysis of how well this candidate fits the role, which transferable skills are most valuable, and what they should focus on developing.");

        var response = await _chatClient.GetResponseAsync(prompt.ToString());
        var text = response.Text?.Trim() ?? "Analysis could not be generated.";
        _logger.LogInformation("StudyController: GraphRAG response generated ({Length} chars).", text.Length);
        return (text, tierSummary);
    }
}
