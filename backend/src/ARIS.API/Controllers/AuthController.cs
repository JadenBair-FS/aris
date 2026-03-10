using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace ARIS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;
    private readonly ArisDbContext _context;

    public AuthController(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<AuthController> logger, ArisDbContext context)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _context = context;
    }

    public class SetRoleRequest
    {
        public required string Role { get; set; }
    }

    /// <summary>
    /// Writes the user's role ("seeker" | "recruiter") into Clerk publicMetadata.
    /// Must be called from the backend because publicMetadata is write-protected on the Clerk client SDK.
    /// The authenticated user's Clerk ID is read from the JWT sub claim.
    /// </summary>
    [Authorize]
    [HttpPost("set-role")]
    public async Task<IActionResult> SetRole([FromBody] SetRoleRequest request)
    {
        if (request.Role != "seeker" && request.Role != "recruiter")
            return BadRequest("Role must be 'seeker' or 'recruiter'.");

        var clerkUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("sub");

        if (string.IsNullOrEmpty(clerkUserId))
            return Unauthorized("Could not determine user identity from token.");

        var secretKey = _config["Clerk:SecretKey"];
        if (string.IsNullOrEmpty(secretKey) || secretKey == "YOUR_CLERK_SECRET_KEY_HERE")
        {
            _logger.LogError("Clerk:SecretKey is not configured.");
            return StatusCode(500, "Auth service is not configured. Set Clerk:SecretKey in appsettings.");
        }

        var client = _httpClientFactory.CreateClient("Clerk");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);

        var body = JsonSerializer.Serialize(new
        {
            public_metadata = new { role = request.Role }
        });

        var response = await client.PatchAsync(
            $"users/{clerkUserId}",
            new StringContent(body, Encoding.UTF8, "application/json")
        );

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Clerk PATCH /users/{UserId} failed: {Status} {Error}", clerkUserId, response.StatusCode, error);
            return StatusCode(500, "Failed to update role in auth provider.");
        }

        _logger.LogInformation("Set role='{Role}' for Clerk user {UserId}", request.Role, clerkUserId);

        if (request.Role == "seeker")
        {
            var seekerUser = await _context.SeekerUsers.FirstOrDefaultAsync(s => s.ClerkId == clerkUserId);
            if (seekerUser == null)
            {
                seekerUser = new SeekerUser { ClerkId = clerkUserId };
                _context.SeekerUsers.Add(seekerUser);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Created seeker_users record for {ClerkId}", clerkUserId);
            }

            var profileExists = await _context.UserProfiles.AnyAsync(p => p.UserId == clerkUserId);
            if (!profileExists)
            {
                _context.UserProfiles.Add(new UserProfile
                {
                    UserId = clerkUserId,
                    SeekerUserId = seekerUser.Id
                });
                await _context.SaveChangesAsync();
                _logger.LogInformation("Created empty user_profiles row for seeker {ClerkId}", clerkUserId);
            }
        }
        else if (request.Role == "recruiter")
        {
            var recruiterUser = await _context.RecruiterUsers.FirstOrDefaultAsync(r => r.ClerkId == clerkUserId);
            if (recruiterUser == null)
            {
                recruiterUser = new RecruiterUser { ClerkId = clerkUserId };
                _context.RecruiterUsers.Add(recruiterUser);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Created recruiter_users record for {ClerkId}", clerkUserId);
            }
        }

        return Ok(new { message = "Role set successfully.", role = request.Role });
    }

    /// <summary>
    /// Deletes the authenticated user's data from all ARIS tables, then removes them from Clerk.
    /// </summary>
    [Authorize]
    [HttpDelete("account")]
    public async Task<IActionResult> DeleteAccount()
    {
        var clerkUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("sub");

        if (string.IsNullOrEmpty(clerkUserId))
            return Unauthorized("Could not determine user identity from token.");

        var seekerUser = await _context.SeekerUsers.FirstOrDefaultAsync(s => s.ClerkId == clerkUserId);
        if (seekerUser != null)
        {
            var profiles = _context.UserProfiles.Where(p => p.SeekerUserId == seekerUser.Id);
            _context.UserProfiles.RemoveRange(profiles);
            _context.SeekerUsers.Remove(seekerUser);
        }

        var recruiterUser = await _context.RecruiterUsers.FirstOrDefaultAsync(r => r.ClerkId == clerkUserId);
        if (recruiterUser != null)
        {
            var jobs = _context.JobPostings.Where(j => j.RecruiterUserId == recruiterUser.Id);
            _context.JobPostings.RemoveRange(jobs);
            _context.RecruiterUsers.Remove(recruiterUser);
        }

        await _context.SaveChangesAsync();

        var secretKey = _config["Clerk:SecretKey"];
        if (!string.IsNullOrEmpty(secretKey) && secretKey != "YOUR_CLERK_SECRET_KEY_HERE")
        {
            var client = _httpClientFactory.CreateClient("Clerk");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
            var clerkResponse = await client.DeleteAsync($"users/{clerkUserId}");
            if (!clerkResponse.IsSuccessStatusCode)
            {
                var error = await clerkResponse.Content.ReadAsStringAsync();
                _logger.LogError("Clerk DELETE /users/{UserId} failed: {Status} {Error}", clerkUserId, clerkResponse.StatusCode, error);
            }
        }

        _logger.LogInformation("Deleted account for Clerk user {ClerkId}", clerkUserId);
        return Ok(new { message = "Account deleted." });
    }
}
