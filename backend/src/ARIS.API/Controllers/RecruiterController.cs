using ARIS.API.Services;
using ARIS.Shared.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace ARIS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RecruiterController : ControllerBase
{
    private readonly ArisDbContext _context;
    private readonly MatchService _matchService;
    private readonly ILogger<RecruiterController> _logger;

    public RecruiterController(ArisDbContext context, MatchService matchService, ILogger<RecruiterController> logger)
    {
        _context = context;
        _matchService = matchService;
        _logger = logger;
    }

    /// <summary>
    /// Bidirectional candidate search — finds the best-matching candidate profiles for a given job posting.
    /// Ranks candidates by pgvector cosine similarity against the job embedding, then runs
    /// AnalyzeMatchAsync on each of the top N to provide gap analysis per candidate.
    /// </summary>
    [HttpGet("job/{jobId:guid}/candidates")]
    public async Task<IActionResult> FindCandidatesForJob(Guid jobId, [FromQuery] int limit = 10)
    {
        var job = await _context.JobPostings.FindAsync(jobId);
        if (job == null)
            return NotFound($"Job {jobId} not found.");

        if (job.Embedding == null)
            return BadRequest($"Job {jobId} has no embedding — ensure it has been processed.");

        _logger.LogInformation("Finding candidates for Job: {JobId}, Limit: {Limit}", jobId, limit);

        var topCandidates = await _context.UserProfiles
            .Where(u => u.Embedding != null)
            .Select(u => new
            {
                u.Id,
                u.UserId,
                SampleRole = u.CleanSignal != null
                    ? u.CleanSignal.Roles.FirstOrDefault() != null
                        ? u.CleanSignal.Roles.First().Title
                        : "Unknown"
                    : "Unknown",
                Distance = u.Embedding!.CosineDistance(job.Embedding)
            })
            .OrderBy(u => u.Distance)
            .Take(limit)
            .ToListAsync();

        if (topCandidates.Count == 0)
            return Ok(new { jobId, candidates = Array.Empty<object>() });

        var candidateResults = new List<object>();
        foreach (var candidate in topCandidates)
        {
            var analysis = await _matchService.AnalyzeMatchAsync(candidate.Id, jobId);
            candidateResults.Add(new
            {
                userProfileId = candidate.Id,
                userId = candidate.UserId,
                primaryRole = candidate.SampleRole,
                vectorSimilarity = 1.0 - candidate.Distance,
                matchAnalysis = analysis
            });
        }

        return Ok(new { jobId, candidates = candidateResults });
    }
}
