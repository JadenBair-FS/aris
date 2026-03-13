using ARIS.API.Services;
using ARIS.Shared.Data;
using ARIS.Shared.Models;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IChatClient? _openAiClient;
    private readonly IMemoryCache _cache;
    private readonly ResumePdfService _pdfService;
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
        ResumePdfService pdfService,
        ILogger<StudyController> logger,
        IServiceProvider serviceProvider)
    {
        _matchService = matchService;
        _jobService = jobService;
        _resumeService = resumeService;
        _graphService = graphService;
        _context = context;
        _embeddingGenerator = embeddingGenerator;
        _chatClient = chatClient;
        _cache = cache;
        _pdfService = pdfService;
        _logger = logger;
        _openAiClient = serviceProvider.GetKeyedService<IChatClient>("openai");
    }

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

        _logger.LogInformation("StudyController.GenerateComparison: running ChatGPT + ARIS tailoring in parallel.");

        var chatGptTask = GenerateChatGptTailoredResumeAsync(request.ResumeText, request.JobDescriptionText);
        var arisTask    = GenerateArisTailoredResumeFromSignalsAsync(
            resumeEntry.Signal, resumeEntry.Embedding,
            jobEntry.Signal, jobEntry.Embedding,
            request.ResumeText, request.JobDescriptionText);

        await Task.WhenAll(chatGptTask, arisTask);

        var (arisResume, tierSummary) = arisTask.Result;

        return Ok(new StudyCompareResponse
        {
            RagResponse      = chatGptTask.Result,
            GraphRagResponse = arisResume,
            TierSummary      = tierSummary
        });
    }


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

        _logger.LogInformation("StudyController.Compare: starting parallel ChatGPT + ARIS tailoring.");

        var chatGptTask = GenerateChatGptTailoredResumeAsync(request.ResumeText, request.JobDescriptionText);
        var arisTask    = GenerateArisTailoredResumeAsync(request.ResumeText, request.JobDescriptionText);

        await Task.WhenAll(chatGptTask, arisTask);

        var (arisResume, tierSummary) = arisTask.Result;

        return Ok(new StudyCompareResponse
        {
            RagResponse      = chatGptTask.Result,
            GraphRagResponse = arisResume,
            TierSummary      = tierSummary
        });
    }


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

        (ResumeCleanSignal Signal, Pgvector.Vector Embedding) cachedResume = default;
        (JobPostingCleanSignal Signal, Pgvector.Vector Embedding) cachedJob = default;

        bool resumeFromCache = !string.IsNullOrWhiteSpace(request.ResumeKey)
            && _cache.TryGetValue($"study:resume:{request.ResumeKey}", out cachedResume);

        bool jobFromCache = !string.IsNullOrWhiteSpace(request.JobKey)
            && _cache.TryGetValue($"study:job:{request.JobKey}", out cachedJob);

        if (resumeFromCache && jobFromCache)
        {
            _logger.LogInformation("StudyController.Tailor: using cached signals (resumeKey={R}, jobKey={J}).",
                request.ResumeKey, request.JobKey);

            resumeSignal     = cachedResume.Signal;
            resumeEmbedding  = cachedResume.Embedding;
            jobSignal        = cachedJob.Signal;
            jobEmbedding     = cachedJob.Embedding;
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

    /// <summary>
    /// Given a cached resume session key (or raw resume text as fallback), queries the database
    /// for job postings with embeddings, runs ARIS tier analysis against the top 10 nearest jobs,
    /// and returns the top 5 results by ArisScore. No data is written to the database.
    /// </summary>
    [HttpPost("match-preview")]
    public async Task<IActionResult> MatchPreview([FromBody] StudyMatchPreviewRequest request)
    {
        ResumeCleanSignal? resumeSignal;
        Pgvector.Vector? resumeEmbedding;

        (ResumeCleanSignal Signal, Pgvector.Vector Embedding) cachedResume = default;

        bool fromCache = !string.IsNullOrWhiteSpace(request.ResumeKey)
            && _cache.TryGetValue($"study:resume:{request.ResumeKey}", out cachedResume);

        if (fromCache)
        {
            resumeSignal    = cachedResume.Signal;
            resumeEmbedding = cachedResume.Embedding;
            _logger.LogInformation("StudyController.MatchPreview: using cached resume signal (key={Key}).", request.ResumeKey);
        }
        else if (!string.IsNullOrWhiteSpace(request.ResumeText))
        {
            _logger.LogInformation("StudyController.MatchPreview: extracting resume signal from text.");
            (resumeSignal, resumeEmbedding) = await _resumeService.QuickExtractResumeSignalAsync(request.ResumeText);
        }
        else
        {
            return BadRequest("Either ResumeKey or ResumeText is required.");
        }

        if (resumeSignal == null || resumeEmbedding == null)
            return StatusCode(500, "Resume signal extraction failed. Please check your input and try again.");

        var candidates = await _context.JobPostings
            .Where(j => j.Embedding != null && j.CleanSignal != null)
            .Select(j => new
            {
                j.Id,
                j.CleanSignal,
                j.Embedding,
                Distance = j.Embedding!.CosineDistance(resumeEmbedding)
            })
            .OrderBy(j => j.Distance)
            .Take(10)
            .ToListAsync();

        int totalSearched = candidates.Count;

        if (totalSearched == 0)
        {
            return Ok(new StudyMatchPreviewResponse
            {
                Matches          = [],
                TotalJobsSearched = 0
            });
        }

        var analysisTask = candidates.Select(async c =>
        {
            try
            {
                var matchResult = await _matchService.AnalyzeMatchFromSignalsAsync(
                    resumeSignal, resumeEmbedding,
                    c.CleanSignal!, c.Embedding!);

                if (matchResult == null) return null;

                var jobTitle = c.CleanSignal!.TargetRoles.FirstOrDefault()?.Title ?? "Unknown Role";

                return new StudyMatchPreviewItem
                {
                    JobTitle            = jobTitle,
                    CompanyName         = null,
                    ArisScore           = Math.Round(matchResult.ArisScore, 4),
                    VectorSimilarity    = Math.Round(matchResult.VectorSimilarity, 4),
                    T1Count             = matchResult.MatchingSkills.Count,
                    T2Count             = matchResult.ImplicitlyDiscoveredSkills.Count,
                    T3Count             = matchResult.PrerequisiteMetSkills.Count,
                    T4Count             = matchResult.BridgeableSkills.Count,
                    T5Count             = matchResult.HardGaps.Count,
                    TopMatchingSkills   = matchResult.MatchingSkills.Take(3).Select(s => s.SkillName).ToList(),
                    TopMissingSkills    = matchResult.HardGaps.Take(3).Select(s => s.SkillName).ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StudyController.MatchPreview: analysis failed for one job posting — skipping.");
                return null;
            }
        });

        var allResults = await Task.WhenAll(analysisTask);

        var top5 = allResults
            .Where(r => r != null)
            .OrderByDescending(r => r!.ArisScore)
            .Take(5)
            .ToList();

        _logger.LogInformation("StudyController.MatchPreview: returning {Count} match(es) from {Total} candidates.", top5.Count, totalSearched);

        return Ok(new StudyMatchPreviewResponse
        {
            Matches           = top5!,
            TotalJobsSearched = totalSearched
        });
    }



    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze([FromBody] StudyAnalyzeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ResumeText))
            return BadRequest("ResumeText is required.");
        if (string.IsNullOrWhiteSpace(request.JobDescriptionText))
            return BadRequest("JobDescriptionText is required.");

        _logger.LogInformation("StudyController.Analyze: extracting signals in parallel.");

        var resumeTask = _resumeService.QuickExtractResumeSignalAsync(request.ResumeText);
        var jobTask    = _jobService.QuickExtractAndGroundAsync(request.JobDescriptionText);

        await Task.WhenAll(resumeTask, jobTask);

        var (resumeSignal, resumeEmbedding) = resumeTask.Result;
        var (jobSignal, jobEmbedding)        = jobTask.Result;

        if (resumeSignal == null || resumeEmbedding == null)
            return BadRequest("Resume signal extraction failed. Please check your input and try again.");
        if (jobSignal == null || jobEmbedding == null)
            return BadRequest("Job signal extraction failed. Please check your input and try again.");

        var matchResult = await _matchService.AnalyzeMatchFromSignalsAsync(
            resumeSignal, resumeEmbedding, jobSignal, jobEmbedding);

        if (matchResult == null)
            return StatusCode(500, "Tier classification failed.");

        var resumeKey = Guid.NewGuid().ToString("N");
        var jobKey    = Guid.NewGuid().ToString("N");
        var ttlOptions = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = SessionTtl };
        _cache.Set($"study:resume:{resumeKey}", (resumeSignal, resumeEmbedding), ttlOptions);
        _cache.Set($"study:job:{jobKey}", (jobSignal, jobEmbedding), ttlOptions);
        _cache.Set($"study:raw-resume:{resumeKey}", request.ResumeText, ttlOptions);
        _cache.Set($"study:raw-job:{jobKey}", request.JobDescriptionText, ttlOptions);

        return Ok(new StudyAnalyzeResponse
        {
            ArisScore                   = Math.Round(matchResult.ArisScore, 4),
            VectorSimilarity            = Math.Round(matchResult.VectorSimilarity, 4),
            MatchingSkills              = matchResult.MatchingSkills,
            ImplicitlyDiscoveredSkills  = matchResult.ImplicitlyDiscoveredSkills,
            PrerequisiteMetSkills       = matchResult.PrerequisiteMetSkills,
            BridgeableSkills            = matchResult.BridgeableSkills,
            HardGaps                    = matchResult.HardGaps,
            SessionResumeKey            = resumeKey,
            SessionJobKey               = jobKey,
        });
    }

    /// <summary>
    /// Like Analyze, but skips resume text extraction and loads the user's existing CleanSignal
    /// and Embedding from the database by UserId. Extracts the job signal from raw text, runs
    /// match analysis, and generates ARIS vs ChatGPT tailored resumes in parallel.
    /// Nothing is written to the database.
    /// </summary>
    [HttpPost("analyze-with-profile")]
    public async Task<IActionResult> AnalyzeWithProfile([FromBody] AnalyzeWithProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobDescriptionText))
            return BadRequest("JobDescriptionText is required.");

        _logger.LogInformation("StudyController.AnalyzeWithProfile: loading user profile {UserId}.", request.UserId);

        var user = await _context.UserProfiles.FindAsync(request.UserId);

        if (user?.CleanSignal == null || user.Embedding == null)
            return NotFound("User profile not found or not yet processed.");

        // Extract raw resume text from the stored RawResume JSON (same pattern as EvalController).
        string resumeText;
        try
        {
            resumeText = System.Text.Json.JsonDocument.Parse(user.RawResume ?? "{}")
                .RootElement.TryGetProperty("content", out var c)
                    ? c.GetString() ?? ""
                    : user.RawResume ?? "";
        }
        catch
        {
            resumeText = user.RawResume ?? "";
        }

        var resumeSignal    = user.CleanSignal;
        var resumeEmbedding = user.Embedding;

        _logger.LogInformation("StudyController.AnalyzeWithProfile: extracting job signal.");

        var (jobSignal, jobEmbedding) = await _jobService.QuickExtractAndGroundAsync(request.JobDescriptionText);

        if (jobSignal == null || jobEmbedding == null)
            return BadRequest("Job signal extraction failed.");

        var matchResult = await _matchService.AnalyzeMatchFromSignalsAsync(
            resumeSignal, resumeEmbedding, jobSignal, jobEmbedding);

        if (matchResult == null)
            return StatusCode(500, "Tier classification failed.");

        var resumeKey = Guid.NewGuid().ToString("N");
        var jobKey    = Guid.NewGuid().ToString("N");
        var ttlOptions = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = SessionTtl };
        _cache.Set($"study:resume:{resumeKey}", (resumeSignal, resumeEmbedding), ttlOptions);
        _cache.Set($"study:job:{jobKey}", (jobSignal, jobEmbedding), ttlOptions);
        _cache.Set($"study:raw-resume:{resumeKey}", resumeText, ttlOptions);
        _cache.Set($"study:raw-job:{jobKey}", request.JobDescriptionText, ttlOptions);

        return Ok(new StudyAnalyzeResponse
        {
            ArisScore                  = Math.Round(matchResult.ArisScore, 4),
            VectorSimilarity           = Math.Round(matchResult.VectorSimilarity, 4),
            MatchingSkills             = matchResult.MatchingSkills,
            ImplicitlyDiscoveredSkills = matchResult.ImplicitlyDiscoveredSkills,
            PrerequisiteMetSkills      = matchResult.PrerequisiteMetSkills,
            BridgeableSkills           = matchResult.BridgeableSkills,
            HardGaps                   = matchResult.HardGaps,
            SessionResumeKey           = resumeKey,
            SessionJobKey              = jobKey,
        });
    }

    /// <summary>
    /// Generates the ARIS and ChatGPT tailored resumes using cached session signals.
    /// Called immediately after Analyze so the match UI can appear without waiting for tailoring.
    /// </summary>
    [HttpPost("tailor-resumes")]
    public async Task<IActionResult> TailorResumes([FromBody] StudyTailorResumesRequest request)
    {
        if (!_cache.TryGetValue($"study:resume:{request.SessionResumeKey}",
                out (ResumeCleanSignal Signal, Pgvector.Vector Embedding) cachedResume))
            return BadRequest(new { error = "Session expired or invalid. Please re-run the analysis." });

        if (!_cache.TryGetValue($"study:job:{request.SessionJobKey}",
                out (JobPostingCleanSignal Signal, Pgvector.Vector Embedding) cachedJob))
            return BadRequest(new { error = "Session expired or invalid. Please re-run the analysis." });

        var resumeText = _cache.Get<string>($"study:raw-resume:{request.SessionResumeKey}") ?? "";
        var jobText    = _cache.Get<string>($"study:raw-job:{request.SessionJobKey}") ?? "";

        _logger.LogInformation("StudyController.TailorResumes: running ChatGPT + ARIS tailoring in parallel.");

        var chatGptTask = GenerateChatGptTailoredResumeAsync(resumeText, jobText);
        var arisTask    = GenerateArisTailoredResumeFromSignalsAsync(
            cachedResume.Signal, cachedResume.Embedding,
            cachedJob.Signal, cachedJob.Embedding,
            resumeText, jobText);

        await Task.WhenAll(chatGptTask, arisTask);
        var (arisResume, _) = arisTask.Result;
        var chatGptResume = chatGptTask.Result;

        var arisPdfTask    = Task.Run(() => _pdfService.GeneratePlainTextPdf(arisResume));
        var chatGptPdfTask = Task.Run(() => _pdfService.GeneratePlainTextPdf(chatGptResume));
        await Task.WhenAll(arisPdfTask, chatGptPdfTask);

        return Ok(new StudyTailorResumesResponse
        {
            ArisResume             = arisResume,
            ChatGptResume          = chatGptResume,
            ArisResumePdfBase64    = Convert.ToBase64String(arisPdfTask.Result),
            ChatGptResumePdfBase64 = Convert.ToBase64String(chatGptPdfTask.Result),
        });
    }

    [HttpPost("explain")]
    public async Task<IActionResult> Explain([FromBody] StudyExplainRequest request)
    {
        // ResumeText is only required when no session key is available (fallback path).
        // In profile mode the resume signal comes from cache; raw text is not needed.
        bool hasResumeKey = !string.IsNullOrWhiteSpace(request.ResumeKey);
        if (!hasResumeKey && string.IsNullOrWhiteSpace(request.ResumeText))
            return BadRequest("ResumeText is required.");
        if (string.IsNullOrWhiteSpace(request.JobDescriptionText))
            return BadRequest("JobDescriptionText is required.");

        ResumeCleanSignal? resumeSignal;
        Pgvector.Vector? resumeEmbedding;
        JobPostingCleanSignal? jobSignal;
        Pgvector.Vector? jobEmbedding;

        (ResumeCleanSignal Signal, Pgvector.Vector Embedding) cachedResume = default;
        (JobPostingCleanSignal Signal, Pgvector.Vector Embedding) cachedJob = default;

        bool resumeFromCache = !string.IsNullOrWhiteSpace(request.ResumeKey)
            && _cache.TryGetValue($"study:resume:{request.ResumeKey}", out cachedResume);
        bool jobFromCache = !string.IsNullOrWhiteSpace(request.JobKey)
            && _cache.TryGetValue($"study:job:{request.JobKey}", out cachedJob);

        if (resumeFromCache && jobFromCache)
        {
            resumeSignal    = cachedResume.Signal;
            resumeEmbedding = cachedResume.Embedding;
            jobSignal       = cachedJob.Signal;
            jobEmbedding    = cachedJob.Embedding;
        }
        else
        {
            _logger.LogInformation("StudyController.Explain: re-extracting signals (no valid cache keys).");
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

        var graphContext  = _graphService.BuildTailoringGraphContext(matchResult, resumeSignal.Skills);
        var scorePercent  = (int)Math.Round(matchResult.ArisScore * 100);
        var scoreLabel    = matchResult.ArisScore >= 0.65 ? "strong" : matchResult.ArisScore >= 0.40 ? "moderate" : "weak";

        var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "MatchExplain.md");
        var prompt = (await System.IO.File.ReadAllTextAsync(promptPath))
            .Replace("{scorePercent}", scorePercent.ToString())
            .Replace("{scoreLabel}", scoreLabel)
            .Replace("{graphContext}", graphContext);

        _logger.LogInformation("StudyController.Explain: calling LLM for explanation.");
        var response = await _chatClient.GetResponseAsync(prompt);
        var explanation = response.Text?.Trim() ?? "Explanation could not be generated.";

        return Ok(new StudyExplainResponse { Explanation = explanation });
    }

    private async Task<string> GenerateChatGptTailoredResumeAsync(string resumeText, string jobText)
    {
        if (_openAiClient == null)
            return "ChatGPT baseline unavailable: OPENAI_API_KEY is not configured.";

        try
        {
            var templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "ChatGptTailoring.md");
            var template = await System.IO.File.ReadAllTextAsync(templatePath);

            var prompt = template
                .Replace("{rawResumeText}", resumeText)
                .Replace("{rawJobText}", jobText);

            var response = await _openAiClient.GetResponseAsync(prompt);
            var text = response.Text?.Trim() ?? "";
            _logger.LogInformation("StudyController: ChatGPT tailored resume generated ({Length} chars).", text.Length);
            return string.IsNullOrWhiteSpace(text) ? "Tailored resume could not be generated." : text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StudyController: ChatGPT tailoring failed.");
            return "System A tailoring is temporarily unavailable.";
        }
    }

    private async Task<(string Response, StudyTierSummary TierSummary)> GenerateArisTailoredResumeAsync(
        string resumeText, string jobDescriptionText)
    {
        var emptySummary = new StudyTierSummary();
        try
        {
            var resumeTask = _resumeService.QuickExtractResumeSignalAsync(resumeText);
            var jobTask    = _jobService.QuickExtractAndGroundAsync(jobDescriptionText);
            await Task.WhenAll(resumeTask, jobTask);

            var (resumeSignal, resumeEmbedding) = resumeTask.Result;
            var (jobSignal, jobEmbedding)        = jobTask.Result;

            if (resumeSignal == null || resumeEmbedding == null)
                return ("ARIS tailoring is temporarily unavailable — resume signal could not be extracted.", emptySummary);
            if (jobSignal == null || jobEmbedding == null)
                return ("ARIS tailoring is temporarily unavailable — job signal could not be extracted.", emptySummary);

            return await BuildArisTailoredResumeAsync(
                resumeSignal, resumeEmbedding, jobSignal, jobEmbedding,
                resumeText, jobDescriptionText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StudyController: ARIS tailoring path failed.");
            return ("ARIS tailoring is temporarily unavailable.", emptySummary);
        }
    }

    private async Task<(string Response, StudyTierSummary TierSummary)> GenerateArisTailoredResumeFromSignalsAsync(
        ResumeCleanSignal resumeSignal, Pgvector.Vector resumeEmbedding,
        JobPostingCleanSignal jobSignal, Pgvector.Vector jobEmbedding,
        string resumeText, string jobDescriptionText)
    {
        try
        {
            return await BuildArisTailoredResumeAsync(
                resumeSignal, resumeEmbedding, jobSignal, jobEmbedding,
                resumeText, jobDescriptionText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StudyController: ARIS tailoring (pre-extracted) failed.");
            return ("ARIS tailoring is temporarily unavailable.", new StudyTierSummary());
        }
    }

    private async Task<(string Response, StudyTierSummary TierSummary)> BuildArisTailoredResumeAsync(
        ResumeCleanSignal resumeSignal, Pgvector.Vector resumeEmbedding,
        JobPostingCleanSignal jobSignal, Pgvector.Vector jobEmbedding,
        string resumeText, string jobDescriptionText)
    {
        var matchResult = await _matchService.AnalyzeMatchFromSignalsAsync(
            resumeSignal, resumeEmbedding, jobSignal, jobEmbedding);

        if (matchResult == null)
        {
            _logger.LogWarning("StudyController: AnalyzeMatchFromSignalsAsync returned null.");
            return ("ARIS tailoring is temporarily unavailable — tier classification failed.", new StudyTierSummary());
        }

        var tierSummary = new StudyTierSummary
        {
            Tier1Count       = matchResult.MatchingSkills.Count,
            Tier2Count       = matchResult.ImplicitlyDiscoveredSkills.Count,
            Tier3Count       = matchResult.PrerequisiteMetSkills.Count,
            Tier4Count       = matchResult.BridgeableSkills.Count,
            VectorSimilarity = Math.Round(matchResult.VectorSimilarity, 4)
        };

        var graphContextBlock = _graphService.BuildTailoringGraphContext(matchResult, resumeSignal.Skills);

        var tailoringTemplatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "ResumeTailoring.md");
        var summaryTemplatePath   = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "ResumeSummary.md");
        var tailoringTemplate = await System.IO.File.ReadAllTextAsync(tailoringTemplatePath);
        var summaryTemplate   = await System.IO.File.ReadAllTextAsync(summaryTemplatePath);

        var resumeSnippet = resumeText.Length > 3000 ? resumeText[..3000] : resumeText;
        var jobSnippet    = jobDescriptionText.Length > 2000 ? jobDescriptionText[..2000] : jobDescriptionText;
        var summaryPrompt = summaryTemplate
            .Replace("{rawResumeSnippet}", resumeSnippet)
            .Replace("{rawJobSnippet}", jobSnippet)
            .Replace("{graphContext}", graphContextBlock);

        string summary;
        try
        {
            var summaryResponse = await _chatClient.GetResponseAsync(summaryPrompt);
            summary = summaryResponse.Text?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StudyController: ARIS summary generation failed.");
            summary = "";
        }

        var experienceEntries = resumeSignal.ExperienceSummary
            .Where(e => e.Bullets.Any(b => !string.IsNullOrWhiteSpace(b)))
            .ToList();

        var bulletOptions = new ChatOptions { Temperature = 0.15f };
        var rewrittenEntries = new List<(string Role, string Company, List<string> Bullets)>();

        foreach (var exp in experienceEntries)
        {
            var bulletsText = string.Join("\n", exp.Bullets.Select((b, i) => $"{i + 1}. {b}"));
            var bulletPrompt = tailoringTemplate
                .Replace("{rawResumeText}", resumeText)
                .Replace("{rawJobText}", jobDescriptionText)
                .Replace("{graphContext}", graphContextBlock)
                .Replace("{role}", exp.Role)
                .Replace("{company}", exp.Company ?? "")
                .Replace("{bullets}", bulletsText);

            var rewritten = new List<string>();
            try
            {
                var response = await _chatClient.GetResponseAsync(bulletPrompt, bulletOptions);
                var raw  = response?.Text?.Trim() ?? "";
                var json = System.Text.RegularExpressions.Regex.Replace(raw, @"```(?:json)?", "").Trim();
                var si   = json.IndexOf('[');
                var ei   = json.LastIndexOf(']');
                if (si >= 0 && ei > si) json = json[si..(ei + 1)];

                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<BulletItem>>(
                    json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (parsed != null)
                    rewritten = parsed
                        .Where(item => !string.IsNullOrWhiteSpace(item.Rewritten))
                        .Select(item => item.Rewritten!)
                        .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StudyController: bullet rewriting failed for role {Role}.", exp.Role);
                rewritten = exp.Bullets.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
            }

            rewrittenEntries.Add((exp.Role, exp.Company ?? "", rewritten));
        }

        var skillsSectionMatch = System.Text.RegularExpressions.Regex.Match(
            resumeText,
            @"(?im)^(SKILLS?[^\n]*)\n(.*?)(?=\n[A-Z][A-Z\s]{2,}:?\s*$|\z)",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var skillsBlock = skillsSectionMatch.Success ? skillsSectionMatch.Value.Trim() : "";

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine(summary);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(skillsBlock))
        {
            sb.AppendLine(skillsBlock);
            sb.AppendLine();
        }
        foreach (var (role, company, bullets) in rewrittenEntries)
        {
            sb.AppendLine(string.IsNullOrWhiteSpace(company) ? role : $"{role} at {company}");
            foreach (var bullet in bullets)
                sb.AppendLine(bullet);
            sb.AppendLine();
        }

        var fullText = sb.ToString().TrimEnd();
        _logger.LogInformation("StudyController: ARIS tailored resume assembled ({Length} chars).", fullText.Length);
        return (string.IsNullOrWhiteSpace(fullText) ? "Tailored resume could not be generated." : fullText, tierSummary);
    }

    private record BulletItem(string? Original, string? Rewritten);
}
