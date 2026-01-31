using ARIS.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace ARIS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ResumeController : ControllerBase
    {
        private readonly ResumeService _service;
        private readonly ILogger<ResumeController> _logger;

        public ResumeController(ResumeService service, ILogger<ResumeController> logger)
        {
            _service = service;
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
    }
}