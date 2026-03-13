using ARIS.Shared.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace ARIS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RecruiterController : ControllerBase
{
    private readonly ArisDbContext _context;
    private readonly ILogger<RecruiterController> _logger;

    public RecruiterController(ArisDbContext context, ILogger<RecruiterController> logger)
    {
        _context = context;
        _logger = logger;
    }

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
                u.CleanSignal,
                Distance = u.Embedding!.CosineDistance(job.Embedding)
            })
            .OrderBy(u => u.Distance)
            .Take(limit)
            .ToListAsync();

        if (topCandidates.Count == 0)
            return Ok(new { jobId, candidates = Array.Empty<object>() });

        var candidateResults = topCandidates.Select(c => new
        {
            userProfileId = c.Id,
            userId = c.UserId,
            primaryRole = c.CleanSignal?.Roles?.FirstOrDefault()?.Title ?? "Unknown",
            vectorSimilarity = 1.0 - c.Distance,
        });

        return Ok(new { jobId, candidates = candidateResults });
    }
}
