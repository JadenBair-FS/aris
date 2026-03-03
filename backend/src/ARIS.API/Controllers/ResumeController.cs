using ARIS.API.Services;
using ARIS.Shared.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ARIS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ResumeController : ControllerBase
    {
        private readonly ResumeService _service;
        private readonly MatchService _matchService;
        private readonly ArisDbContext _context;
        private readonly ILogger<ResumeController> _logger;

        public ResumeController(ResumeService service, MatchService matchService, ArisDbContext context, ILogger<ResumeController> logger)
        {
            _service = service;
            _matchService = matchService;
            _context = context;
            _logger = logger;
        }

        public class ResumeUploadRequest
        {
            public required IFormFile File { get; set; }
        }

        [HttpPost("upload")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadResume([FromForm] ResumeUploadRequest request)
        {
            if (request.File == null || request.File.Length == 0)
                return BadRequest("No file uploaded.");

            if (request.File.ContentType != "application/pdf")
                return BadRequest("Only PDF files are supported.");

            var clerkId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
            if (string.IsNullOrEmpty(clerkId))
                return Unauthorized("Could not determine user identity from token.");

            var seekerUser = await _context.SeekerUsers.FirstOrDefaultAsync(s => s.ClerkId == clerkId);
            if (seekerUser == null)
                return Unauthorized("Seeker account not found. Call /api/auth/set-role first.");

            _logger.LogInformation("Received resume upload for User: {UserId}, Size: {Size}", clerkId, request.File.Length);

            using var stream = request.File.OpenReadStream();
            var profileId = await _service.ProcessResumeAsync(stream, clerkId, seekerUser.Id);

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
            public string? UserId { get; set; }
        }

        [HttpPost("upload-text")]
        [AllowAnonymous]
        public async Task<IActionResult> UploadResumeText([FromBody] ResumeTextRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Content))
                return BadRequest("No content provided.");

            var clerkId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? request.UserId;
            if (string.IsNullOrEmpty(clerkId))
                return Unauthorized("Could not determine user identity from token.");

            var seekerUser = await _context.SeekerUsers.FirstOrDefaultAsync(s => s.ClerkId == clerkId);

            _logger.LogInformation("Received resume text upload for User: {UserId}", clerkId);

            var profileId = await _service.ProcessResumeTextAsync(request.Content, clerkId, seekerUser?.Id);

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
        /// Looks up a user profile by the external auth provider user ID (e.g. Clerk user_xxx string).
        /// Used by the frontend on initial seeker load when only the auth ID is known, not the profile UUID.
        /// </summary>
        [HttpGet("by-user/{userId}")]
        public async Task<IActionResult> GetProfileByUserId(string userId)
        {
            var profile = await _context.UserProfiles
                .FirstOrDefaultAsync(u => u.UserId == userId);

            if (profile == null)
                return NotFound($"No profile found for userId '{userId}'.");

            // CleanSignal is null when the seeker has not yet uploaded a resume.
            // Return the profile shell so the frontend can show the upload prompt.
            return Ok(new
            {
                profile.Id,
                profile.UserId,
                profile.CleanSignal,
                hasResume = profile.CleanSignal != null
            });
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
