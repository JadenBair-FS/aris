using ARIS.API.Services;
using ARIS.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

namespace ARIS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MatchController : ControllerBase
{
    private readonly MatchService _matchService;
    private readonly GraphService _graphService;
    private readonly IChatClient _chatClient;
    private readonly ILogger<MatchController> _logger;

    public MatchController(
        MatchService matchService,
        GraphService graphService,
        IChatClient chatClient,
        ILogger<MatchController> logger)
    {
        _matchService = matchService;
        _graphService = graphService;
        _chatClient = chatClient;
        _logger = logger;
    }

    [HttpPost("analyze")]
    [AllowAnonymous]
    public async Task<IActionResult> AnalyzeMatch([FromBody] MatchRequest request)
    {
        if (request.UserProfileId == Guid.Empty || request.JobId == Guid.Empty)
        {
            return BadRequest("UserProfileId and JobId are required.");
        }

        var result = await _matchService.AnalyzeMatchAsync(request.UserProfileId, request.JobId);

        if (result == null)
            return NotFound("Analysis failed. Ensure UserProfileId and JobId are correct, and that both have been processed (have CleanSignal and Embeddings). Check server logs for specific missing fields.");

        return Ok(result);
    }

    [AllowAnonymous]
    [HttpPost("analyze-quick")]
    public async Task<IActionResult> AnalyzeMatchQuick([FromBody] QuickMatchRequest request)
    {
        if (request.UserProfileId == Guid.Empty || string.IsNullOrWhiteSpace(request.JobDescriptionText))
            return BadRequest("UserProfileId and JobDescriptionText are required.");

        var result = await _matchService.AnalyzeMatchQuickAsync(request.UserProfileId, request.JobDescriptionText);
        if (result == null)
            return NotFound("Analysis failed. Ensure UserProfileId is valid and has a processed resume.");
        return Ok(result);
    }

    [HttpPost("summary")]
    public async Task<IActionResult> GetGroundedSummary([FromBody] MatchRequest request)
    {
        if (request.UserProfileId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("UserProfileId and JobId are required.");

        var (summary, groundingScore) = await _matchService.GenerateGroundedSummaryAsync(request.UserProfileId, request.JobId);
        return Ok(new { summary, groundingScore });
    }

    [AllowAnonymous]
    [HttpPost("recruiter-summary")]
    public async Task<IActionResult> GetRecruiterSummary([FromBody] MatchRequest request)
    {
        if (request.UserProfileId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("UserProfileId and JobId are required.");

        var result = await _matchService.GenerateRecruiterSummaryAsync(request.UserProfileId, request.JobId);
        return Ok(new { summary = result.Summary, groundingScore = result.GroundingScore, verdict = result.Verdict });
    }

    [HttpGet("scores/candidates/{jobId:guid}")]
    public async Task<IActionResult> GetArisScoresCandidates(Guid jobId, [FromQuery] int limit = 20)
    {
        var scores = await _matchService.GetFastScoresCandidatesAsync(jobId, limit);
        if (scores.Count == 0) return NotFound();
        return Ok(new { jobId, scores });
    }

    [HttpGet("scores/jobs/{profileId:guid}")]
    public async Task<IActionResult> GetArisScoresJobPostings(Guid profileId, [FromQuery] int limit = 20)
    {
        var scores = await _matchService.GetFastScoresJobsAsync(profileId, limit);
        return Ok(new { profileId, scores });
    }

    [AllowAnonymous]
    [HttpGet("debug/jobs")]
    public async Task<IActionResult> DebugListJobs()
    {
        return Ok(await _matchService.DebugGetJobsAsync());
    }

    [AllowAnonymous]
    [HttpGet("debug/users")]
    public async Task<IActionResult> DebugListUsers()
    {
        return Ok(await _matchService.DebugGetUsersAsync());
    }

    [AllowAnonymous]
    [HttpGet("debug/user/{id}")]
    public async Task<IActionResult> DebugGetUser(Guid id)
    {
        return Ok(await _matchService.DebugGetUserDetailAsync(id));
    }

    [AllowAnonymous]
    [HttpGet("debug/job/{id}")]
    public async Task<IActionResult> DebugGetJob(Guid id)
    {
        return Ok(await _matchService.DebugGetJobDetailAsync(id));
    }

    [HttpPost("explain")]
    public async Task<IActionResult> ExplainMatch([FromBody] ExplainMatchRequest request)
    {
        if (request.UserProfileId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("UserProfileId and JobId are required.");

        var analysisResult = await _matchService.AnalyzeMatchAsync(request.UserProfileId, request.JobId);
        if (analysisResult == null)
            return NotFound("Analysis failed. Ensure UserProfileId and JobId are correct, and that both have been processed.");

        var graphContext = _graphService.BuildTailoringGraphContext(analysisResult);

        var scorePercent = (int)Math.Round(analysisResult.ArisScore * 100);
        var scoreLabel = analysisResult.ArisScore >= 0.65 ? "strong" : analysisResult.ArisScore >= 0.40 ? "moderate" : "weak";

        var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "MatchExplain.md");
        var prompt = (await System.IO.File.ReadAllTextAsync(promptPath))
            .Replace("{scorePercent}", scorePercent.ToString())
            .Replace("{scoreLabel}", scoreLabel)
            .Replace("{graphContext}", graphContext);

        _logger.LogInformation("MatchController.ExplainMatch: calling LLM for user {UserId} / job {JobId}.", request.UserProfileId, request.JobId);
        var response = await _chatClient.GetResponseAsync(prompt);
        var explanation = response.Text?.Trim() ?? "Explanation could not be generated.";

        return Ok(new { explanation });
    }
}
