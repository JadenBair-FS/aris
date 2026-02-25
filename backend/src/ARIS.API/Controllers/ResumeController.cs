using ARIS.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace ARIS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ResumeController : ControllerBase
    {
        private readonly ResumeService _service;
        private readonly MatchService _matchService;
        private readonly ILogger<ResumeController> _logger;

        public ResumeController(ResumeService service, MatchService matchService, ILogger<ResumeController> logger)
        {
            _service = service;
            _matchService = matchService;
            _logger = logger;
        }

        public class ResumeUploadRequest
        {
            public required IFormFile File { get; set; }
            public required string UserId { get; set; }
        }

        [HttpPost("upload")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadResume([FromForm] ResumeUploadRequest request)
        {
            if (request.File == null || request.File.Length == 0)
                return BadRequest("No file uploaded.");

            if (string.IsNullOrWhiteSpace(request.UserId))
                return BadRequest("User ID is required.");

            if (request.File.ContentType != "application/pdf")
                return BadRequest("Only PDF files are supported.");

            _logger.LogInformation("Received resume upload for User: {UserId}, Size: {Size}", request.UserId, request.File.Length);

            using var stream = request.File.OpenReadStream();
            var profileId = await _service.ProcessResumeAsync(stream, request.UserId);

            if (profileId.HasValue)
            {
                return Ok(new { message = "Resume processed and ingested successfully.", id = profileId.Value });
            }
            else
            {
                return StatusCode(500, "Failed to process resume. Check server logs.");
            }
        }

        public class ResumeTextRequest
        {
            public required string Content { get; set; }
            public required string UserId { get; set; }
        }

        [HttpPost("upload-text")]
        public async Task<IActionResult> UploadResumeText([FromBody] ResumeTextRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Content))
                return BadRequest("No content provided.");

            if (string.IsNullOrWhiteSpace(request.UserId))
                return BadRequest("User ID is required.");

            _logger.LogInformation("Received resume text upload for User: {UserId}", request.UserId);

            var profileId = await _service.ProcessResumeTextAsync(request.Content, request.UserId);

            if (profileId.HasValue)
            {
                return Ok(new { message = "Resume text processed and ingested successfully.", id = profileId.Value });
            }
            else
            {
                return StatusCode(500, "Failed to process resume text. Check server logs.");
            }
        }

        /// <summary>
        /// Returns a user's full CleanSignal and metadata. Used by the frontend job seeker dashboard.
        /// </summary>
        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetUserProfile(Guid id)
        {
            var detail = await _matchService.DebugGetUserDetailAsync(id);
            if (detail == null)
                return NotFound($"User profile {id} not found.");
            return Ok(detail);
        }

        public class TailorRequest
        {
            public Guid UserProfileId { get; set; }
            public Guid JobId { get; set; }
        }

        /// <summary>
        /// Tailors resume bullets to highlight transferability toward bridgeable and prerequisite-met skills.
        /// Returns tailored bullets per gap with the original text and target skill context.
        /// </summary>
        [HttpPost("tailor")]
        public async Task<IActionResult> TailorResume([FromBody] TailorRequest request)
        {
            if (request.UserProfileId == Guid.Empty || request.JobId == Guid.Empty)
                return BadRequest("UserProfileId and JobId are required.");

            var result = await _service.TailorResumeAsync(request.UserProfileId, request.JobId);
            if (result == null)
                return NotFound("Could not tailor resume. Ensure the profile and job exist and have been processed.");

            return Ok(result);
        }
    }
}
