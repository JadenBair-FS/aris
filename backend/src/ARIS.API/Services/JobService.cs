using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using ARIS.Shared.Models;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;
using Pgvector;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace ARIS.API.Services
{
    public class JobService
    {
        private readonly ArisDbContext _context;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
        private readonly IChatClient _chatClient;
        private readonly ILogger<JobService> _logger;

        public JobService(ArisDbContext context, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IChatClient chatClient, ILogger<JobService> logger)
        {
            _context = context;
            _embeddingGenerator = embeddingGenerator;
            _chatClient = chatClient;
            _logger = logger;
        }

        public async Task<Guid?> CreateJobPostingAsync(string rawDescription, string recruiterId)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                };

                var cleanSignal = await ExtractJobCleanSignalAsync(rawDescription, options);
                if (cleanSignal == null)
                {
                    _logger.LogWarning("Failed to extract Clean Signal for job posting by {RecruiterId}", recruiterId);
                    return null;
                }

                await GroundCleanSignalAsync(cleanSignal);

                var symmetricString = BuildSymmetricString(cleanSignal);
                var embeddings = await _embeddingGenerator.GenerateAsync([symmetricString]);
                var vectorData = embeddings[0].Vector;

                var jobPosting = new JobPosting
                {
                    RecruiterId = recruiterId,
                    RawDescription = rawDescription,
                    CleanSignal = cleanSignal,
                    Embedding = new Vector(vectorData),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.JobPostings.Add(jobPosting);
                await _context.SaveChangesAsync();

                return jobPosting.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating job posting for recruiter {RecruiterId}", recruiterId);
                return null;
            }
        }

        public async Task<JobRecommendationResponse> GetRecommendedJobsAsync(Guid userProfileId, int limit = 10)
        {
            var userProfile = await _context.UserProfiles.FindAsync(userProfileId);
            if (userProfile?.Embedding == null || userProfile.CleanSignal == null)
            {
                return new JobRecommendationResponse
                {
                    Matches = [],
                    Analysis = "User profile not found or incomplete."
                };
            }

            var matches = await _context.JobPostings
                .Where(j => j.Embedding != null)
                .Select(j => new
                {
                    Job = j,
                    Distance = j.Embedding!.CosineDistance(userProfile.Embedding)
                })
                .Where(x => x.Distance < 0.65)
                .OrderBy(x => x.Distance)
                .Take(limit)
                .ToListAsync();

            var mappedMatches = matches.Select(x => new JobMatchResult
            {
                JobId = x.Job.Id,
                Job = x.Job,
                Distance = x.Distance,
                Score = 1 - x.Distance
            }).ToList();

            if (mappedMatches.Count == 0)
            {
                return new JobRecommendationResponse
                {
                    Matches = [],
                    Analysis = "No matching jobs found in the database. Try updating your profile or searching for broader roles."
                };
            }

            var analysis = await GenerateMatchAnalysisAsync(userProfile.CleanSignal, [.. mappedMatches.Take(3)]);

            return new JobRecommendationResponse
            {
                Matches = mappedMatches,
                Analysis = analysis
            };
        }

        private async Task<string> GenerateMatchAnalysisAsync(ResumeCleanSignal userProfile, List<JobMatchResult> topMatches)
        {
            try
            {
                var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "MatchAnalysis.md");
                var template = await File.ReadAllTextAsync(promptPath);

                var sbMatches = new StringBuilder();
                foreach (var match in topMatches)
                {
                    if (match.Job?.CleanSignal == null) continue;
                    var signal = match.Job.CleanSignal;
                    sbMatches.AppendLine($"Job: {string.Join("/", signal.TargetRoles.Select(t => t.Title))}");
                    sbMatches.AppendLine($"   Required: {string.Join(", ", signal.RequiredSkills.Select(s => s.Name))}");
                    sbMatches.AppendLine($"   Match Score: {match.Score:P0}");
                    sbMatches.AppendLine();
                }

                var prompt = template
                    .Replace("{candidate_roles}", string.Join(", ", userProfile.Roles.Select(r => r.Title)))
                    .Replace("{candidate_skills}", string.Join(", ", userProfile.Skills.Select(s => s.Name)))
                    .Replace("{job_matches}", sbMatches.ToString());

                var response = await _chatClient.GetResponseAsync(prompt);
                return response.Text ?? "Analysis could not be generated.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate LLM analysis for job matches.");
                return "Analysis temporarily unavailable.";
            }
        }

        private async Task GroundCleanSignalAsync(JobPostingCleanSignal signal)
        {
            var roleTitles = signal.TargetRoles.Select(r => r.Title).ToList();
            var skillNames = signal.RequiredSkills.Select(s => s.Name).ToList();

            if (roleTitles.Count == 0 && skillNames.Count == 0) return;

            var allTexts = roleTitles.Concat(skillNames).ToList();

            try
            {
                var embeddings = await _embeddingGenerator.GenerateAsync(allTexts);
                var vectors = embeddings.Select(e => new Vector(e.Vector)).ToList();

                for (int i = 0; i < roleTitles.Count; i++)
                {
                    var vector = vectors[i];
                    var match = await _context.Roles
                        .Where(r => r.Embedding != null)
                        .Select(r => new { r.Title, Distance = r.Embedding!.CosineDistance(vector) })
                        .OrderBy(x => x.Distance)
                        .FirstOrDefaultAsync();

                    if (match != null && match.Distance < 0.6)
                    {
                        signal.TargetRoles[i].Title = match.Title;
                    }
                }

                int skillOffset = roleTitles.Count;
                for (int i = 0; i < skillNames.Count; i++)
                {
                    var vector = vectors[skillOffset + i];
                    var match = await _context.Skills
                        .Where(s => s.Embedding != null)
                        .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(vector) })
                        .OrderBy(x => x.Distance)
                        .FirstOrDefaultAsync();

                    if (match != null && match.Distance < 0.6)
                    {
                        signal.RequiredSkills[i].Name = match.Name;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ground job signal. Proceeding with ungrounded data.");
            }
        }

        private async Task<(string roles, string skills)> RetrieveReferenceVocabularyAsync(string rawText)
        {
            try
            {
                var embeddings = await _embeddingGenerator.GenerateAsync([rawText]);
                var vector = new Vector(embeddings[0].Vector);

                var topRoles = await _context.Roles
                    .Where(r => r.Embedding != null)
                    .Select(r => new { r.Title, Distance = r.Embedding!.CosineDistance(vector) })
                    .OrderBy(x => x.Distance)
                    .Take(15)
                    .ToListAsync();

                var topSkills = await _context.Skills
                    .Where(s => s.Embedding != null)
                    .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(vector) })
                    .OrderBy(x => x.Distance)
                    .Take(50)
                    .ToListAsync();

                return (
                    string.Join(", ", topRoles.Select(r => r.Title)),
                    string.Join(", ", topSkills.Select(s => s.Name))
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve reference vocabulary. Proceeding without it.");
                return ("", "");
            }
        }

        private async Task<JobPostingCleanSignal?> ExtractJobCleanSignalAsync(string rawText, JsonSerializerOptions options)
        {
            string prompt;
            try
            {
                var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "JobExtraction.md");
                var template = await File.ReadAllTextAsync(promptPath);

                var (refRoles, refSkills) = await RetrieveReferenceVocabularyAsync(rawText);

                prompt = template
                    .Replace("{reference_roles}", refRoles)
                    .Replace("{reference_skills}", refSkills)
                    .Replace("{raw_text}", rawText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load Job Extraction prompt.");
                return null;
            }

            try
            {
                var response = await _chatClient.GetResponseAsync(prompt);
                var jsonString = response?.Text;

                if (string.IsNullOrWhiteSpace(jsonString))
                    return null;

                if (jsonString.Contains("```json"))
                {
                    jsonString = jsonString.Split("```json")[1].Split("```")[0].Trim();
                }
                else if (jsonString.Contains("```"))
                {
                    jsonString = jsonString.Split("```")[1].Split("```")[0].Trim();
                }

                return JsonSerializer.Deserialize<JobPostingCleanSignal>(jsonString, options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM Extraction or Deserialization failed for Job Posting.");
                return null;
            }
        }

        private static string BuildSymmetricString(JobPostingCleanSignal signal)
        {
            var sb = new StringBuilder();

            foreach (var role in signal.TargetRoles)
            {
                sb.Append(role.Title).Append(' ');
            }

            foreach (var skill in signal.RequiredSkills)
            {
                sb.Append(skill.Name).Append(' ');
            }

            return sb.ToString().Trim();
        }
    }
}
