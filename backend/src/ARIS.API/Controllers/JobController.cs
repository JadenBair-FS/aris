using ARIS.API.Services;
using ARIS.Shared.Data;
using ARIS.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ARIS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class JobController : ControllerBase
    {
        private readonly JobService _service;
        private readonly MatchService _matchService;
        private readonly ArisDbContext _context;
        private readonly ILogger<JobController> _logger;

        public JobController(JobService service, MatchService matchService, ArisDbContext context, ILogger<JobController> logger)
        {
            _service = service;
            _matchService = matchService;
            _context = context;
            _logger = logger;
        }

        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> CreateJob([FromBody] CreateJobRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Description))
                return BadRequest("Description is required.");

            var clerkId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
            var recruiterId = clerkId ?? request.RecruiterId;

            if (string.IsNullOrEmpty(recruiterId))
                return BadRequest("Could not determine recruiter identity. Provide a JWT or RecruiterId in the body.");

            _logger.LogInformation("Received job posting from Recruiter: {RecruiterId}", recruiterId);

            var jobId = await _service.CreateJobPostingAsync(request.Description, recruiterId);

            if (!jobId.HasValue)
                return StatusCode(500, "Failed to process job posting.");

            if (!string.IsNullOrEmpty(clerkId))
            {
                var recruiterUser = await _context.RecruiterUsers.FirstOrDefaultAsync(r => r.ClerkId == clerkId);
                if (recruiterUser == null)
                    _logger.LogWarning("No recruiter_users record for {ClerkId}. Job created without FK link. Call /api/auth/set-role.", clerkId);
                else
                {
                    var job = await _context.JobPostings.FindAsync(jobId.Value);
                    if (job != null)
                    {
                        job.RecruiterUserId = recruiterUser.Id;
                        await _context.SaveChangesAsync();
                    }
                }
            }

            return Ok(new { message = "Job processed and ingested successfully.", jobId = jobId.Value });
        }

        [HttpGet("match/{userProfileId}")]
        public async Task<IActionResult> GetMatches(Guid userProfileId)
        {
            _logger.LogInformation("Fetching job matches for UserProfile: {UserProfileId}", userProfileId);

            var response = await _service.GetRecommendedJobsAsync(userProfileId);

            return Ok(response);
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetJob(Guid id)
        {
            var detail = await _matchService.DebugGetJobDetailAsync(id);
            if (detail == null)
                return NotFound($"Job {id} not found.");
            return Ok(detail);
        }

        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> DeleteJob(Guid id)
        {
            var clerkId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
            if (string.IsNullOrEmpty(clerkId))
                return Unauthorized("Could not determine user identity from token.");

            var recruiterUser = await _context.RecruiterUsers.FirstOrDefaultAsync(r => r.ClerkId == clerkId);
            if (recruiterUser == null)
                return Unauthorized("Recruiter account not found.");

            var job = await _context.JobPostings.FirstOrDefaultAsync(j => j.Id == id && j.RecruiterUserId == recruiterUser.Id);
            if (job == null)
                return NotFound("Job posting not found.");

            _context.JobPostings.Remove(job);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Job posting deleted." });
        }

        [HttpGet("by-recruiter")]
        public async Task<IActionResult> GetJobsByRecruiter()
        {
            var clerkId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
            if (string.IsNullOrEmpty(clerkId))
                return Unauthorized("Could not determine user identity from token.");

            var recruiterUser = await _context.RecruiterUsers.FirstOrDefaultAsync(r => r.ClerkId == clerkId);
            if (recruiterUser == null)
                return Unauthorized("Recruiter account not found. Call /api/auth/set-role first.");

            var jobs = await _context.JobPostings
                .Where(j => j.RecruiterUserId == recruiterUser.Id)
                .OrderByDescending(j => j.CreatedAt)
                .Select(j => new
                {
                    j.Id,
                    j.RecruiterId,
                    j.CleanSignal,
                    j.CreatedAt,
                    j.UpdatedAt
                })
                .ToListAsync();

            return Ok(jobs);
        }
    }
}
