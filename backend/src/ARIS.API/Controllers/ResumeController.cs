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
        private readonly PersonalInfoExtractor _personalInfoExtractor;
        private readonly ResumePdfService _resumePdfService;
        private readonly ArisDbContext _context;
        private readonly ILogger<ResumeController> _logger;

        public ResumeController(ResumeService service, MatchService matchService, PersonalInfoExtractor personalInfoExtractor, ResumePdfService resumePdfService, ArisDbContext context, ILogger<ResumeController> logger)
        {
            _service = service;
            _matchService = matchService;
            _personalInfoExtractor = personalInfoExtractor;
            _resumePdfService = resumePdfService;
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

            public List<string>? MatchingSkills { get; set; }
            public List<string>? ImplicitSkills { get; set; }
            public List<string>? PrereqMetSkills { get; set; }
            public List<string>? BridgeableSkills { get; set; }
            public List<string>? HardGaps { get; set; }
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

        [HttpPost("tailor")]
        public async Task<IActionResult> TailorResume([FromBody] TailorRequest request)
        {
            if (request.UserProfileId == Guid.Empty || request.JobId == Guid.Empty)
                return BadRequest("UserProfileId and JobId are required.");

            var result = await _service.TailorResumeAsync(
                request.UserProfileId, request.JobId,
                request.MatchingSkills, request.ImplicitSkills,
                request.PrereqMetSkills, request.BridgeableSkills,
                request.HardGaps);

            if (result == null)
                return NotFound("Could not tailor resume. Ensure the profile and job exist and have been processed.");

            return Ok(result);
        }

        [HttpPost("tailor-pdf")]
        public async Task<IActionResult> TailorResumePdf([FromBody] TailorRequest request)
        {
            if (request.UserProfileId == Guid.Empty || request.JobId == Guid.Empty)
                return BadRequest("UserProfileId and JobId are required.");

            var user = await _context.UserProfiles.FindAsync(request.UserProfileId);
            if (user?.CleanSignal == null)
                return NotFound("User profile not found or has no clean signal.");

            var tailoredBullets = await _service.TailorResumeAsync(
                request.UserProfileId, request.JobId,
                request.MatchingSkills, request.ImplicitSkills,
                request.PrereqMetSkills, request.BridgeableSkills,
                request.HardGaps);

            if (tailoredBullets == null)
                return NotFound("Could not tailor resume. Ensure the profile and job exist and have been processed.");

            var personalInfo = await _personalInfoExtractor.ExtractAsync(user.RawResume);
            var pdfBytes = _resumePdfService.GeneratePdf(personalInfo, user.CleanSignal, tailoredBullets);

            return File(pdfBytes, "application/pdf", "ARIS_Tailored_Resume.pdf");
        }
    }
}
