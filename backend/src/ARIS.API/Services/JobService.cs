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

                // Extract Clean Signal
                var cleanSignal = await ExtractJobCleanSignalAsync(rawDescription, options);
                if (cleanSignal == null)
                {
                    _logger.LogWarning("Failed to extract Clean Signal for job posting by {RecruiterId}", recruiterId);
                    return null;
                }

                // Vectorize
                var symmetricString = BuildSymmetricString(cleanSignal);
                var embeddings = await _embeddingGenerator.GenerateAsync([symmetricString]);
                var vectorData = embeddings[0].Vector;

                // Save
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

            // Semantic Search using Cosine Distance
            // We select both the entity and the distance
            var matches = await _context.JobPostings
                .Where(j => j.Embedding != null)
                .Select(j => new 
                { 
                    Job = j, 
                    Distance = j.Embedding!.CosineDistance(userProfile.Embedding) 
                })
                .Where(x => x.Distance < 0.65) //filter out distant matches 
                .OrderBy(x => x.Distance)
                .Take(limit)
                .ToListAsync();

            var mappedMatches = matches.Select(x => new JobMatchResult
            {
                JobId = x.Job.Id,
                Job = x.Job,
                Distance = x.Distance,
                Score = 1 - x.Distance //convert distance to similarity score
            }).ToList();

            if (mappedMatches.Count == 0)
            {
                return new JobRecommendationResponse
                {
                    Matches = [],
                    Analysis = "No matching jobs found in the database. Try updating your profile or searching for broader roles."
                };
            }

            // Generate LLM Analysis
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
                var sb = new StringBuilder();
                sb.AppendLine("You are an expert Career Counselor. Analyze the fit between the Candidate and these Job Matches.");
                sb.AppendLine("Provide a brief summary of why these are good matches and highlight 1-2 key skill gaps if any.");
                sb.AppendLine();
                
                sb.AppendLine("CANDIDATE PROFILE:");
                sb.AppendLine($"- Roles: {string.Join(", ", userProfile.Roles.Select(r => r.Title))}");
                sb.AppendLine($"- Skills: {string.Join(", ", userProfile.Skills.Select(s => s.Name))}");
                sb.AppendLine();

                sb.AppendLine("TOP JOB MATCHES:");
                foreach (var match in topMatches)
                {
                    if (match.Job?.CleanSignal == null) continue;
                    
                    var signal = match.Job.CleanSignal;
                    sb.AppendLine($"Job: {string.Join("/", signal.TargetRoles.Select(t => t.Title))}");
                    sb.AppendLine($"   Required: {string.Join(", ", signal.RequiredSkills.Select(s => s.Name))}");
                    sb.AppendLine($"   Match Score: {match.Score:P0}");
                    sb.AppendLine();
                }

                sb.AppendLine("ANALYSIS (Keep it concise, under 150 words):");

                var response = await _chatClient.GetResponseAsync(sb.ToString());
                return response.Text ?? "Analysis could not be generated.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate LLM analysis for job matches.");
                return "Analysis temporarily unavailable.";
            }
        }

        private async Task<JobPostingCleanSignal?> ExtractJobCleanSignalAsync(string rawText, JsonSerializerOptions options)
        {
             var prompt = $@"
You are a Recruitment Intelligence Engine. Your task is to parse the following Job Description into a standardized JSON format.
Identify the Core Target Roles and Essential Skills required.

Required JSON Schema:
{{
  ""target_roles"": [ {{ ""title"": ""string"", ""priority"": ""string (Primary/Secondary)"" }} ],
  ""required_skills"": [ {{ ""name"": ""string"", ""importance"": ""string (Essential/Preferred)"" }} ],
  ""responsibilities"": [ ""string"" ],
  ""minimum_education"": [ {{ ""degree"": ""string"", ""required"": ""string (true/false)"" }} ]
}}

JOB DESCRIPTION:
{rawText}

Output ONLY valid JSON. No markdown formatting. No preamble.";

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
            // Concatenate Target Roles and Required Skills
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
