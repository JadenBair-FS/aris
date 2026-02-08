using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;
using Pgvector;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

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

        public async Task<Guid?> ProcessResumeAsync(Stream fileStream, string userId)
        {
            try
            {
                var rawText = ExtractTextFromPdf(fileStream);
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    _logger.LogWarning("PDF parsing resulted in empty text for user {UserId}", userId);
                    return null;
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                };

                var cleanSignal = await ExtractCleanSignalAsync(rawText, options);
                if (cleanSignal == null)
                {
                    _logger.LogError("Failed to extract Clean Signal for user {UserId}", userId);
                    return null;
                }

                await GroundCleanSignalAsync(cleanSignal);

                var symmetricString = BuildSymmetricString(cleanSignal);
                var embeddings = await _embeddingGenerator.GenerateAsync([symmetricString]);
                var vectorData = embeddings[0].Vector;

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

                return userProfile.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing resume for user {UserId}", userId);
                return null;
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

        private async Task<ResumeCleanSignal?> ExtractCleanSignalAsync(string rawText, JsonSerializerOptions options)
        {
            string prompt;
            try
            {
                var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "ResumeExtraction.md");
                var template = await File.ReadAllTextAsync(promptPath);

                var (refRoles, refSkills) = await RetrieveReferenceVocabularyAsync(rawText);

                prompt = template
                    .Replace("{reference_roles}", refRoles)
                    .Replace("{reference_skills}", refSkills)
                    .Replace("{raw_text}", rawText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load prompt template.");
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

                return JsonSerializer.Deserialize<ResumeCleanSignal>(jsonString, options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM Extraction or Deserialization failed.");
                return null;
            }
        }

        private async Task GroundCleanSignalAsync(ResumeCleanSignal signal)
        {
            var roleTitles = signal.Roles.Select(r => r.Title).ToList();
            var skillNames = signal.Skills.Select(s => s.Name).ToList();

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
                        signal.Roles[i].Title = match.Title;
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
                        signal.Skills[i].Name = match.Name;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ground resume signal. Proceeding with ungrounded data.");
            }
        }

        private static string BuildSymmetricString(ResumeCleanSignal signal)
        {
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