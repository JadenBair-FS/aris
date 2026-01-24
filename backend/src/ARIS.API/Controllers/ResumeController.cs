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

        [HttpPost("upload")]
        public async Task<IActionResult> UploadResume([FromForm] IFormFile file, [FromForm] string userId)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded.");

            if (string.IsNullOrWhiteSpace(userId))
                return BadRequest("User ID is required.");

            if (file.ContentType != "application/pdf")
                return BadRequest("Only PDF files are supported.");

            _logger.LogInformation("Received resume upload for User: {UserId}, Size: {Size}", userId, file.Length);

            using var stream = file.OpenReadStream();
            var result = await _service.ProcessResumeAsync(stream, userId);

            if (result)
            {
                return Ok(new { message = "Resume processed and ingested successfully." });
            }
            else
            {
                return StatusCode(500, "Failed to process resume. Check server logs.");
            }
        }
    }
}