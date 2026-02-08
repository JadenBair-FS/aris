using ARIS.Shared.Data;
using ARIS.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace ARIS.API.Services;

public class MatchService
{
    private readonly ArisDbContext _context;
    private readonly GraphService _graphService;
    private readonly ILogger<MatchService> _logger;

    public MatchService(ArisDbContext context, GraphService graphService, ILogger<MatchService> logger)
    {
        _context = context;
        _graphService = graphService;
        _logger = logger;
    }

    public async Task<MatchAnalysisResult?> AnalyzeMatchAsync(Guid userProfileId, Guid jobId)
    {
        var user = await _context.UserProfiles.FindAsync(userProfileId);
        var job = await _context.JobPostings.FindAsync(jobId);

        if (user == null) 
        {
            _logger.LogWarning("Match analysis failed: User {UserId} not found.", userProfileId);
            return null;
        }
        if (job == null)
        {
             _logger.LogWarning("Match analysis failed: Job {JobId} not found.", jobId);
             return null;
        }
        if (user.CleanSignal == null)
        {
            _logger.LogWarning("Match analysis failed: User {UserId} has no CleanSignal.", userProfileId);
            return null;
        }
        if (job.CleanSignal == null)
        {
            _logger.LogWarning("Match analysis failed: Job {JobId} has no CleanSignal.", jobId);
            return null;
        }
        if (user.Embedding == null)
        {
             _logger.LogWarning("Match analysis failed: User {UserId} has no Embedding.", userProfileId);
             return null;
        }
        if (job.Embedding == null)
        {
            _logger.LogWarning("Match analysis failed: Job {JobId} has no Embedding.", jobId);
            return null;
        }

        var distance = await _context.JobPostings
            .Where(j => j.Id == jobId)
            .Select(j => j.Embedding!.CosineDistance(user.Embedding))
            .FirstOrDefaultAsync();
        
        var similarity = 1 - distance;

        var userSkills = user.CleanSignal.Skills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var jobSkills = job.CleanSignal.RequiredSkills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var implicitSkills = await _graphService.GetImplicitlyDiscoveredSkillsAsync(userSkills);
        
        var totalUserSkills = new HashSet<string>(userSkills, StringComparer.OrdinalIgnoreCase);
        foreach (var s in implicitSkills) totalUserSkills.Add(s);

        var matchingSkills = jobSkills.Intersect(userSkills).ToList();
        var implicitlyMatched = jobSkills.Intersect(implicitSkills).Except(userSkills).ToList();
        var missingSkills = jobSkills.Except(totalUserSkills).ToList();

        var bridgeable = new List<string>();
        var prerequisiteMet = new List<string>();
        var hardGaps = new List<string>();

        if (missingSkills.Count > 0)
        {
            var neighborhood = await _graphService.GetValidNeighborhoodAsync(totalUserSkills);
            var prerequisiteMetSet = await _graphService.GetPrerequisiteMetSkillsAsync(totalUserSkills, missingSkills);

            _logger.LogInformation("Graph Neighborhood for User: {Neighborhood}", string.Join(", ", neighborhood));
            _logger.LogInformation("Prerequisite-Met Skills: {PrereqMet}", string.Join(", ", prerequisiteMetSet));

            foreach (var skill in missingSkills)
            {
                if (prerequisiteMetSet.Contains(skill))
                {
                    prerequisiteMet.Add(skill);
                }
                else if (neighborhood.Contains(skill))
                {
                    bridgeable.Add(skill);
                }
                else
                {
                    hardGaps.Add(skill);
                }
            }
        }

        return new MatchAnalysisResult
        {
            JobId = job.Id,
            VectorSimilarity = similarity,
            MatchingSkills = matchingSkills,
            ImplicitlyDiscoveredSkills = implicitlyMatched,
            MissingSkills = missingSkills,
            BridgeableSkills = bridgeable,
            PrerequisiteMetSkills = prerequisiteMet,
            HardGaps = hardGaps
        };
    }

    public async Task<object> DebugGetJobsAsync()
    {
        var jobs = await _context.JobPostings
            .OrderByDescending(j => j.CreatedAt)
            .Take(10)
            .ToListAsync();

        return jobs.Select(j => new { 
            j.Id, 
            Recruiter = j.RecruiterId, 
            SampleRole = j.CleanSignal?.TargetRoles?.FirstOrDefault()?.Title ?? "Unknown",
            HasEmbedding = j.Embedding != null
        });
    }

    public async Task<object> DebugGetUsersAsync()
    {
        var users = await _context.UserProfiles
            .OrderByDescending(u => u.UpdatedAt)
            .Take(10)
            .ToListAsync();

        return users.Select(u => new {
            u.Id,
            u.UserId,
            SampleRole = u.CleanSignal?.Roles?.FirstOrDefault()?.Title ?? "Unknown",
            HasEmbedding = u.Embedding != null
        });
    }

    public async Task<object?> DebugGetUserDetailAsync(Guid id)
    {
        var user = await _context.UserProfiles.FindAsync(id);
        if (user == null) return null;
        return new { user.Id, user.UserId, user.CleanSignal };
    }

    public async Task<object?> DebugGetJobDetailAsync(Guid id)
    {
        var job = await _context.JobPostings.FindAsync(id);
        if (job == null) return null;
        return new { job.Id, job.RecruiterId, job.CleanSignal };
    }
}
