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
    public class ResumeController : ControllerBase
    {
        private readonly ResumeService _service;
        private readonly MatchService _matchService;
        private readonly ResumePdfService _resumePdfService;
        private readonly ArisDbContext _context;
        private readonly ILogger<ResumeController> _logger;
        private readonly HashSet<string> _lockedUserIds;

        public ResumeController(ResumeService service, MatchService matchService, ResumePdfService resumePdfService, ArisDbContext context, ILogger<ResumeController> logger, IConfiguration configuration)
        {
            _service = service;
            _matchService = matchService;
            _resumePdfService = resumePdfService;
            _context = context;
            _logger = logger;
            _lockedUserIds = configuration.GetSection("Study:LockedUserIds").Get<List<string>>()?.ToHashSet() ?? [];
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

            if (_lockedUserIds.Contains(clerkId))
                return StatusCode(403, "This account's resume is read-only.");

            var seekerUser = await _context.SeekerUsers.FirstOrDefaultAsync(s => s.ClerkId == clerkId);
            if (seekerUser == null)
                return Unauthorized("Seeker account not found. Call /api/auth/set-role first.");

            _logger.LogInformation("Received resume upload for User: {UserId}, Size: {Size}", clerkId, request.File.Length);

            using var stream = request.File.OpenReadStream();
            var profileId = await _service.ProcessResumeAsync(stream, clerkId, seekerUser.Id);

            if (profileId.HasValue)
            {
                var profile = await _context.UserProfiles.FindAsync(profileId.Value);
                return Ok(new { message = "Resume processed and ingested successfully.", id = profileId.Value, cleanSignal = profile?.CleanSignal });
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

            if (_lockedUserIds.Contains(clerkId))
                return StatusCode(403, "This account's resume is read-only.");

            var seekerUser = await _context.SeekerUsers.FirstOrDefaultAsync(s => s.ClerkId == clerkId);

            _logger.LogInformation("Received resume text upload for User: {UserId}", clerkId);

            var profileId = await _service.ProcessResumeTextAsync(request.Content, clerkId, seekerUser?.Id);

            if (profileId.HasValue)
            {
                var profile = await _context.UserProfiles.FindAsync(profileId.Value);
                return Ok(new { message = "Resume text processed and ingested successfully.", id = profileId.Value, cleanSignal = profile?.CleanSignal });
            }
            else
            {
                return StatusCode(500, "Failed to process resume text. Check server logs.");
            }
        }

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

        [HttpGet("by-user/{userId}")]
        public async Task<IActionResult> GetProfileByUserId(string userId)
        {
            var profile = await _context.UserProfiles
                .FirstOrDefaultAsync(u => u.UserId == userId);

            if (profile == null)
                return NotFound($"No profile found for userId '{userId}'.");

            return Ok(new
            {
                profile.Id,
                profile.UserId,
                profile.CleanSignal,
                hasResume = profile.CleanSignal != null
            });
        }

        [HttpDelete]
        public async Task<IActionResult> DeleteResume()
        {
            var clerkId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
            if (string.IsNullOrEmpty(clerkId))
                return Unauthorized("Could not determine user identity from token.");

            if (_lockedUserIds.Contains(clerkId))
                return StatusCode(403, "This account's resume is read-only.");

            var profile = await _context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == clerkId);
            if (profile == null)
                return NotFound("Profile not found.");

            profile.RawResume = null;
            profile.CleanSignal = null;
            profile.Embedding = null;
            profile.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Resume cleared." });
        }

        public class GroundingRequest
        {
            public List<GroundingCorrectionItem> Corrections { get; set; } = [];
        }

        [HttpPatch("{id:guid}/grounding")]
        public async Task<IActionResult> ApplyGrounding(Guid id, [FromBody] GroundingRequest request)
        {
            var updatedSignal = await _service.ApplyGroundingCorrectionsAsync(id, request.Corrections);
            if (updatedSignal == null)
                return NotFound($"Profile {id} not found or has no clean signal.");

            return Ok(new { message = "Grounding corrections applied.", cleanSignal = updatedSignal });
        }

        [HttpPost("tailor-pdf")]
        public async Task<IActionResult> TailorResumePdf([FromBody] TailorRequest request)
        {
            if (request.UserProfileId == Guid.Empty || request.JobId == Guid.Empty)
                return BadRequest("UserProfileId and JobId are required.");

            var data = await _service.BuildTailoredResumeDataAsync(
                request.UserProfileId, request.JobId);

            if (data == null)
                return NotFound("Could not tailor resume. Ensure the profile and job exist and have been processed.");

            var pdfBytes = _resumePdfService.GeneratePdf(
                data.PersonalInfo,
                data.CleanSignal,
                data.ProfessionalSummary,
                data.TailoredBullets);

            return File(pdfBytes, "application/pdf", "ARIS_Tailored_Resume.pdf");
        }
    }
}
