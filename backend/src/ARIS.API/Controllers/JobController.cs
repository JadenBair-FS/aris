using ARIS.API.Services;
using ARIS.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace ARIS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class JobController : ControllerBase
    {
        private readonly JobService _service;
        private readonly ILogger<JobController> _logger;

        public JobController(JobService service, ILogger<JobController> logger)
        {
            _service = service;
            _logger = logger;
        }

     
        [HttpPost]
        public async Task<IActionResult> CreateJob([FromBody] CreateJobRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Description) || string.IsNullOrWhiteSpace(request.RecruiterId))
                return BadRequest("Description and RecruiterId are required.");

            _logger.LogInformation("Received job posting from Recruiter: {RecruiterId}", request.RecruiterId);

            var jobId = await _service.CreateJobPostingAsync(request.Description, request.RecruiterId);

            if (jobId.HasValue)
            {
                return Ok(new { message = "Job processed and ingested successfully.", jobId = jobId.Value });
            }
            else
            {
                return StatusCode(500, "Failed to process job posting.");
            }
        }

        [HttpGet("match/{userProfileId}")]
        public async Task<IActionResult> GetMatches(Guid userProfileId)
        {
            _logger.LogInformation("Fetching job matches for UserProfile: {UserProfileId}", userProfileId);

            var response = await _service.GetRecommendedJobsAsync(userProfileId);
            
            return Ok(response);
        }
    }
}
