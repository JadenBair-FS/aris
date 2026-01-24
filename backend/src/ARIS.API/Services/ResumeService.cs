using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;
using Pgvector;

namespace ARIS.API.Services
{
    public class ResumeService
    {
        private readonly ArisDbContext _context;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
        private readonly IChatClient _chatClient;
        private readonly ILogger<ResumeService> _logger;


        public ResumeService(ArisDbContext context, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IChatClient chatClient, ILogger<ResumeService> logger)
        {
            _context = context;
            _embeddingGenerator = embeddingGenerator;
            _chatClient = chatClient;
            _logger = logger;
        }

        public async Task<bool> ProcessResumeAsync(Stream fileStream, string userId)
        {
            try
            {
                // Parse PDF
                var rawText = ExtractTextFromPdf(fileStream);
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    _logger.LogWarning("PDF parsing resulted in empty text for user {UserId}", userId);
                    return false;
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                };

                // Extract Clean Signal with LLM
                var cleanSignal = await ExtractCleanSignalAsync(rawText, options);
                if (cleanSignal == null)
                {
                    _logger.LogError("Failed to extract Clean Signal for user {UserId}", userId);
                    return false;
                }

                // Generate Embedding
                var symmetricString = BuildSymmetricString(cleanSignal);
                var embeddings = await _embeddingGenerator.GenerateAsync([symmetricString]);
                var vectorData = embeddings[0].Vector;

                // Save to Database
                var userProfile = new UserProfile
                {
                    UserId = userId,
                    RawResume = JsonSerializer.Serialize(new { content = rawText }), 
                    CleanSignal = cleanSignal,
                    Embedding = new Vector(vectorData),
                    UpdatedAt = DateTime.UtcNow
                };

                _context.UserProfiles.Add(userProfile);
                await _context.SaveChangesAsync();

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing resume for user {UserId}", userId);
                return false;
            }
        }

        private string ExtractTextFromPdf(Stream stream)
        {
            var sb = new StringBuilder();
            try
            {
                using var document = PdfDocument.Open(stream);
                foreach (var page in document.GetPages())
                {
                    sb.Append(page.Text);
                    sb.Append(' ');
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PdfPig failed to parse the stream.");
                return string.Empty;
            }
            return sb.ToString().Trim();
        }

        private async Task<ResumeCleanSignal?> ExtractCleanSignalAsync(string rawText, JsonSerializerOptions options)
        {
            var prompt = $@"
You are a strict Data Extraction Engine. Your task is to parse the following Resume Text into a standardized JSON format.
Ignore all subjective prose, formatting, and non-technical fluff.

Required JSON Schema:
{{
  ""roles"": [ {{ ""title"": ""string"", ""duration"": ""string (e.g. '2 years', '6 months')"", ""is_current"": ""string (true/false)"" }} ],
  ""skills"": [ {{ ""name"": ""string"", ""category"": ""string"", ""proficiency"": ""string"" }} ],
  ""experience_summary"": [ {{ ""role"": ""string"", ""company"": ""string"", ""bullets"": [""string""] }} ],
  ""education"": [ {{ ""degree"": ""string"", ""institution"": ""string"", ""year"": ""string"" }} ]
}}

RESUME TEXT:
{rawText}

Output ONLY valid JSON. No markdown formatting. No preamble.";

            try
            {
              
                var response = await _chatClient.GetResponseAsync(prompt);
                var jsonString = response?.Text;

                if (string.IsNullOrWhiteSpace(jsonString))
                    return null;

                // Clean potential markdown code blocks if the LLM adds them
                if (jsonString.Contains("```json"))
                {
                    jsonString = jsonString.Split("```json")[1].Split("```")[0].Trim();
                }
                else if (jsonString.Contains("```"))
                {
                     jsonString = jsonString.Split("```")[1].Split("```")[0].Trim();
                }

                return JsonSerializer.Deserialize<ResumeCleanSignal>(jsonString, options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM Extraction or Deserialization failed.");
                return null;
            }
        }

        private static string BuildSymmetricString(ResumeCleanSignal signal)
        {
            // Concatenate Roles and Skills for the vector embedding
            // This ensures the vector represents the "Professional Identity"
            var sb = new StringBuilder();

            foreach (var role in signal.Roles)
            {
                sb.Append(role.Title).Append(' ');
            }

            foreach (var skill in signal.Skills)
            {
                sb.Append(skill.Name).Append(' ');
            }

            return sb.ToString().Trim();
        }
    }
}