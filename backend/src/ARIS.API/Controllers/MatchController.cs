using ARIS.API.Services;
using ARIS.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace ARIS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MatchController : ControllerBase
{
    private readonly MatchService _matchService;

    public MatchController(MatchService matchService)
    {
        _matchService = matchService;
    }

    [HttpPost("analyze")]
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

    /// <summary>
    /// Generates a grounded match summary using graph-path context injected into the LLM prompt.
    /// Returns the narrative summary and the graph grounding score (thesis RQ2 metric).
    /// </summary>
    [HttpPost("summary")]
    public async Task<IActionResult> GetGroundedSummary([FromBody] MatchRequest request)
    {
        if (request.UserProfileId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("UserProfileId and JobId are required.");

        var (summary, groundingScore) = await _matchService.GenerateGroundedSummaryAsync(request.UserProfileId, request.JobId);
        return Ok(new { summary, groundingScore });
    }

    [HttpGet("debug/jobs")]
    public async Task<IActionResult> DebugListJobs()
    {
        return Ok(await _matchService.DebugGetJobsAsync());
    }

    [HttpGet("debug/users")]
    public async Task<IActionResult> DebugListUsers()
    {
        return Ok(await _matchService.DebugGetUsersAsync());
    }

    [HttpGet("debug/user/{id}")]
    public async Task<IActionResult> DebugGetUser(Guid id)
    {
        return Ok(await _matchService.DebugGetUserDetailAsync(id));
    }

    [HttpGet("debug/job/{id}")]
    public async Task<IActionResult> DebugGetJob(Guid id)
    {
        return Ok(await _matchService.DebugGetJobDetailAsync(id));
    }
}
