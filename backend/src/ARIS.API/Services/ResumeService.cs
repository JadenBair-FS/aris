using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using ARIS.Shared.Helpers;
using ARIS.Shared.Models;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UglyToad.PdfPig;
using Pgvector;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace ARIS.API.Services
{
    internal sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var parts = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var item = reader.TokenType switch
                    {
                        JsonTokenType.String => reader.GetString() ?? "",
                        JsonTokenType.Number when reader.TryGetInt64(out var l) => l.ToString(),
                        JsonTokenType.Number => reader.GetDouble().ToString(),
                        JsonTokenType.True => "true",
                        JsonTokenType.False => "false",
                        _ => ""
                    };
                    if (item.Length > 0) parts.Add(item);
                }
                return string.Join(" ", parts);
            }

            return reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number when reader.TryGetInt64(out var l) => l.ToString(),
                JsonTokenType.Number => reader.GetDouble().ToString(),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => null,
                _ => null
            };
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }

    internal sealed class LenientDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.Null => 0.0,
                JsonTokenType.String when double.TryParse(reader.GetString(), out var d) => d,
                JsonTokenType.String => 0.0,
                JsonTokenType.Number => reader.GetDouble(),
                _ => 0.0
            };
        }

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }

    public class ResumeService
    {
        private readonly ArisDbContext _context;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
        private readonly IChatClient _chatClient;
        private readonly ILogger<ResumeService> _logger;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Converters = { new LenientStringConverter(), new LenientDoubleConverter() }
        };

        private const int MaxExtractionAttempts = 3;

        public ResumeService(ArisDbContext context, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IChatClient chatClient, ILogger<ResumeService> logger)
        {
            _context = context;
            _embeddingGenerator = embeddingGenerator;
            _chatClient = chatClient;
            _logger = logger;
        }

        public async Task<Guid?> ProcessResumeAsync(Stream fileStream, string userId, Guid? seekerUserId = null)
        {
            try
            {
                var rawText = ExtractTextFromPdf(fileStream);
                return await ProcessResumeTextAsync(rawText, userId, seekerUserId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing resume for user {UserId}", userId);
                return null;
            }
        }

        public async Task<Guid?> ProcessResumeTextAsync(string rawText, string userId, Guid? seekerUserId = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    _logger.LogWarning("Resume text is empty for user {UserId}", userId);
                    return null;
                }

                rawText = StripReferences(rawText);
                rawText = SanitizePdfText(rawText);

                var cleanSignal = await ExtractCleanSignalAsync(rawText);
                if (cleanSignal == null)
                {
                    _logger.LogError("Failed to extract Clean Signal for user {UserId}", userId);
                    return null;
                }

                await GroundCleanSignalAsync(cleanSignal);

                var symmetricString = BuildSymmetricString(cleanSignal);
                var truncatedSymmetric = symmetricString.Length > 2000 ? symmetricString[..2000] : symmetricString;
                var embeddings = await _embeddingGenerator.GenerateAsync([truncatedSymmetric]);
                var vectorData = embeddings[0].Vector;

                var existing = await _context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
                if (existing != null)
                {
                    existing.RawResume = JsonSerializer.Serialize(new { content = rawText });
                    existing.CleanSignal = cleanSignal;
                    existing.Embedding = new Vector(vectorData);
                    existing.UpdatedAt = DateTime.UtcNow;
                    if (seekerUserId.HasValue)
                        existing.SeekerUserId = seekerUserId;
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Updated existing profile {ProfileId} for user {UserId}", existing.Id, userId);
                    return existing.Id;
                }

                var userProfile = new UserProfile
                {
                    UserId = userId,
                    RawResume = JsonSerializer.Serialize(new { content = rawText }),
                    CleanSignal = cleanSignal,
                    Embedding = new Vector(vectorData),
                    UpdatedAt = DateTime.UtcNow,
                    SeekerUserId = seekerUserId
                };

                _context.UserProfiles.Add(userProfile);
                await _context.SaveChangesAsync();

                return userProfile.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing resume text for user {UserId}", userId);
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
                var truncatedText = rawText.Length > 6000 ? rawText[..6000] : rawText;
                var embeddings = await _embeddingGenerator.GenerateAsync([truncatedText]);
                var vector = new Vector(embeddings[0].Vector);

                var topRoles = await _context.Roles
                    .Where(r => r.Embedding != null)
                    .Select(r => new { r.Title, r.OnetCode, Distance = r.Embedding!.CosineDistance(vector) })
                    .OrderBy(x => x.Distance)
                    .Take(8)
                    .ToListAsync();

                var bestRole = topRoles.FirstOrDefault();
                bool isTech = DomainClassifier.IsTechDomain(bestRole?.OnetCode);
                string? domainPrefix = bestRole?.OnetCode?.Contains('-') == true
                    ? bestRole.OnetCode.Split('-')[0]
                    : null;

                List<string> domainSkillNames = [];
                if (domainPrefix != null)
                {
                    var domainSkills = await _context.RoleSkills
                        .Include(rs => rs.Skill)
                        .Include(rs => rs.Role)
                        .Where(rs => rs.Role.OnetCode != null
                                  && rs.Role.OnetCode.StartsWith(domainPrefix)
                                  && rs.Skill.Embedding != null)
                        .Select(rs => new { rs.Skill.Name, Distance = rs.Skill.Embedding!.CosineDistance(vector) })
                        .OrderBy(x => x.Distance)
                        .Take(15)
                        .ToListAsync();
                    domainSkillNames = domainSkills.Select(s => s.Name).ToList();
                }

                var skillQuery = _context.Skills.Where(s => s.Embedding != null);
                if (!isTech)
                    skillQuery = skillQuery.Where(s => s.Source != "Roadmap.sh");

                var domainSet = new HashSet<string>(domainSkillNames, StringComparer.OrdinalIgnoreCase);

                var globalSkillNames = (await skillQuery
                    .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(vector) })
                    .OrderBy(x => x.Distance)
                    .Take(30)
                    .ToListAsync())
                    .Where(s => !domainSet.Contains(s.Name))
                    .Select(s => s.Name)
                    .Take(15)
                    .ToList();

                var combinedSkills = domainSkillNames.Concat(globalSkillNames).ToList();

                return (
                    string.Join(", ", topRoles.Select(r => r.Title)),
                    string.Join(", ", combinedSkills)
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve reference vocabulary. Proceeding without it.");
                return ("", "");
            }
        }

        private async Task<ResumeCleanSignal?> ExtractCleanSignalAsync(string rawText)
        {
            string userPrompt;
            try
            {
                var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "ResumeExtraction.md");
                var template = await File.ReadAllTextAsync(promptPath);

                var (refRoles, refSkills) = await RetrieveReferenceVocabularyAsync(rawText);

                userPrompt = template
                    .Replace("{reference_roles}", refRoles)
                    .Replace("{reference_skills}", refSkills)
                    .Replace("{raw_text}", rawText);

                _logger.LogInformation("Reference Roles Sent: {Roles}", refRoles);
                _logger.LogInformation("Reference Skills Sent: {Skills}", refSkills);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load prompt template.");
                return null;
            }

            string? previousOutput = null;
            string? validationFailure = null;

            for (int attempt = 1; attempt <= MaxExtractionAttempts; attempt++)
            {
                try
                {
                    var messages = BuildExtractionMessages(userPrompt, attempt, validationFailure, previousOutput);
                    
                    var chatOptions = new ChatOptions 
                    { 
                        ResponseFormat = ChatResponseFormat.Json, 
                        Temperature = 0.1f,
                        AdditionalProperties = new AdditionalPropertiesDictionary
                        {
                            ["stream"] = false,
                            ["num_ctx"] = 4096,
                            ["num_gpu"] = 35,
                            ["num_thread"] = 8
                        }
                    };

                    var response = await _chatClient.GetResponseAsync(messages, chatOptions);
                    var jsonString = response?.Text?.Trim();

                    if (string.IsNullOrWhiteSpace(jsonString))
                    {
                        _logger.LogWarning("LLM returned empty response (attempt {Attempt})", attempt);
                        continue;
                    }

                    _logger.LogInformation("LLM Response (attempt {Attempt}): {Json}", attempt, jsonString);

                    jsonString = UnwrapIfNeeded(jsonString);

                    jsonString = NormalizeFieldNames(jsonString);

                    var signal = JsonSerializer.Deserialize<ResumeCleanSignal>(jsonString, _jsonOptions);
                    if (signal == null)
                    {
                        _logger.LogWarning("Deserialization returned null (attempt {Attempt})", attempt);
                        previousOutput = jsonString;
                        validationFailure = "Deserialization produced a null object.";
                        continue;
                    }

                    PostProcessCleanSignal(signal);

                    var validation = ValidateCleanSignal(signal);
                    if (validation == null)
                    {
                        _logger.LogInformation("Clean Signal extraction succeeded on attempt {Attempt}", attempt);
                        return signal;
                    }

                    _logger.LogWarning("Validation failed (attempt {Attempt}): {Reason}", attempt, validation);
                    previousOutput = jsonString;
                    validationFailure = validation;
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "JSON parsing failed (attempt {Attempt})", attempt);
                    validationFailure = $"JSON parse error: {ex.Message}";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "LLM extraction failed (attempt {Attempt})", attempt);
                    return null;
                }
            }

            _logger.LogError("Clean Signal extraction failed after {MaxAttempts} attempts", MaxExtractionAttempts);
            return null;
        }

        private static List<ChatMessage> BuildExtractionMessages(string userPrompt, int attempt, string? validationFailure, string? previousOutput)
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You are a resume-to-JSON extraction engine. You MUST output a single JSON object with exactly these 4 top-level keys: \"roles\", \"skills\", \"experience_summary\", \"education\". No other keys are allowed. Do not nest the result inside a wrapper object. For every skill: \"years_of_experience\" must always be a number — NEVER null, use 0.0 if unknown. All \"year\" values must be strings (e.g. \"2019\", not 2019)."),
                new(ChatRole.User, userPrompt)
            };

            if (attempt > 1 && validationFailure != null && previousOutput != null)
            {
                messages.Add(new ChatMessage(ChatRole.User, BuildCorrectionPrompt(validationFailure, previousOutput)));
            }

            return messages;
        }

        private static string BuildCorrectionPrompt(string validationFailure, string previousOutput)
        {
            return $$"""
                Your previous output failed validation: {{validationFailure}}

                Previous output:
                {{previousOutput}}

                Please fix the issue and output the corrected JSON using EXACTLY this schema (pay attention to the property names inside each object):

                {
                  "roles": [{ "title": "string", "duration": "string", "is_current": true/false }],
                  "skills": [{ "name": "string", "category": "string", "proficiency": "string", "years_of_experience": 0.0 }],
                  "experience_summary": [{ "role": "string", "company": "string", "bullets": ["string"] }],
                  "education": [{ "degree": "string", "institution": "string", "year": "string" }]
                }

                Critical field requirements:
                - roles[].title MUST be a string (not "name")
                - skills[].years_of_experience MUST be a number — NEVER null, use 0.0 if unknown
                - experience_summary[] entries MUST use "role" (not "title"), "company", "bullets" (array of strings)
                - education[].year MUST be a string, e.g. "2019" not 2019
                """;
        }

        private static string? ValidateCleanSignal(ResumeCleanSignal signal)
        {
            if (signal.Skills.Count == 0)
                return "No skills were extracted. Every resume should have at least one technical skill.";

            return null;
        }

        private static void PostProcessCleanSignal(ResumeCleanSignal signal)
        {
            signal.Roles.RemoveAll(r => string.IsNullOrWhiteSpace(r.Title));
            foreach (var role in signal.Roles)
            {
                role.Title = (role.Title ?? "").Trim();
                role.Duration = (role.Duration ?? "").Trim();
            }

            signal.Skills.RemoveAll(s => string.IsNullOrWhiteSpace(s.Name));
            foreach (var skill in signal.Skills)
            {
                skill.Name = (skill.Name ?? "").Trim();
                skill.Category = (skill.Category ?? "").Trim();
                skill.Proficiency = (skill.Proficiency ?? "").Trim();
            }

            signal.ExperienceSummary.RemoveAll(e => string.IsNullOrWhiteSpace(e.Role) && string.IsNullOrWhiteSpace(e.Company));
            foreach (var exp in signal.ExperienceSummary)
            {
                exp.Role = (exp.Role ?? "").Trim();
                exp.Company = (exp.Company ?? "").Trim();
                exp.Bullets.RemoveAll(string.IsNullOrWhiteSpace);
            }

            signal.Education.RemoveAll(e => string.IsNullOrWhiteSpace(e.Degree) && string.IsNullOrWhiteSpace(e.Institution));
            foreach (var edu in signal.Education)
            {
                edu.Degree = (edu.Degree ?? "").Trim();
                edu.Institution = (edu.Institution ?? "").Trim();
                edu.Year = (edu.Year ?? "").Trim();
            }
        }

        private static string NormalizeFieldNames(string jsonString)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonString);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return jsonString;

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();

                    foreach (var prop in root.EnumerateObject())
                    {
                        writer.WritePropertyName(prop.Name);

                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            writer.WriteStartArray();
                            foreach (var item in prop.Value.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.Object)
                                {
                                    var renames = GetFieldRenames(prop.Name);
                                    WriteNormalizedObject(writer, item, renames);
                                }
                                else
                                {
                                    item.WriteTo(writer);
                                }
                            }
                            writer.WriteEndArray();
                        }
                        else
                        {
                            prop.Value.WriteTo(writer);
                        }
                    }

                    writer.WriteEndObject();
                }

                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch
            {
                return jsonString;
            }
        }

        private static Dictionary<string, string> GetFieldRenames(string arrayKey)
        {
            return arrayKey switch
            {
                "roles" => new Dictionary<string, string>
                {
                    ["name"] = "title",
                    ["position"] = "title",
                    ["job_title"] = "title",
                },
                "skills" => new Dictionary<string, string>
                {
                    ["yoe"] = "years_of_experience",
                    ["experience"] = "years_of_experience",
                    ["years"] = "years_of_experience",
                    ["duration"] = "years_of_experience",
                },
                "experience_summary" => new Dictionary<string, string>
                {
                    ["title"] = "role",
                    ["position"] = "role",
                    ["job_title"] = "role",
                    ["organization"] = "company",
                    ["institution"] = "company",
                    ["employer"] = "company",
                    ["description"] = "bullets",
                    ["summary"] = "bullets",
                    ["details"] = "bullets",
                    ["dates"] = "company",
                },
                "education" => new Dictionary<string, string>
                {
                    ["university"] = "institution",
                    ["school"] = "institution",
                    ["college"] = "institution",
                    ["qualification"] = "degree",
                    ["field"] = "degree",
                    ["graduation_date"] = "year",
                    ["date"] = "year",
                },
                _ => new Dictionary<string, string>()
            };
        }

        private static void WriteNormalizedObject(Utf8JsonWriter writer, JsonElement element, Dictionary<string, string> renames)
        {
            writer.WriteStartObject();

            var written = new HashSet<string>();

            foreach (var prop in element.EnumerateObject())
            {
                var key = renames.TryGetValue(prop.Name.ToLower(), out var renamed) ? renamed : prop.Name;

                if (!written.Add(key)) continue;

                if (key == "bullets" && prop.Value.ValueKind == JsonValueKind.String)
                {
                    writer.WritePropertyName(key);
                    writer.WriteStartArray();
                    writer.WriteStringValue(prop.Value.GetString());
                    writer.WriteEndArray();
                    continue;
                }

                writer.WritePropertyName(key);
                prop.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        private static string UnwrapIfNeeded(string jsonString)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonString);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    return jsonString;

                var properties = root.EnumerateObject().ToList();
                if (properties.Count == 1 && properties[0].Value.ValueKind == JsonValueKind.Object)
                {
                    var inner = properties[0].Value;
                    if (inner.TryGetProperty("roles", out _) || inner.TryGetProperty("skills", out _))
                    {
                        return inner.GetRawText();
                    }
                }
            }
            catch
            {
            }

            return jsonString;
        }

        private async Task GroundCleanSignalAsync(ResumeCleanSignal signal)
        {
            var roleTitles = signal.Roles.Select(r => r.Title).ToList();
            var skillNames = signal.Skills.Select(s => s.Name).ToList();

            if (roleTitles.Count == 0 && skillNames.Count == 0) return;

            var allTexts = roleTitles.Concat(skillNames)
                .Select(t => t.Length > 1000 ? t[..1000] : t)
                .ToList();

            try
            {
                var embeddings = await _embeddingGenerator.GenerateAsync(allTexts);
                var vectors = embeddings.Select(e => new Vector(e.Vector)).ToList();

                for (int i = 0; i < roleTitles.Count; i++)
                {
                    var vector = vectors[i];
                    var match = await _context.Roles
                        .Where(r => r.Embedding != null)
                        .Select(r => new { r.Title, r.OnetCode, Distance = r.Embedding!.CosineDistance(vector) })
                        .OrderBy(x => x.Distance)
                        .FirstOrDefaultAsync();

                    if (match != null && match.Distance < 0.35)
                    {
                        signal.Roles[i].Title = match.Title;
                        signal.Roles[i].OnetCode = match.OnetCode;
                    }
                }

                var primaryRole = signal.Roles.FirstOrDefault(r => r.IsCurrent) ?? signal.Roles.FirstOrDefault();
                string? domainPrefix = null;
                if (primaryRole?.OnetCode != null && primaryRole.OnetCode.Contains('-'))
                {
                    domainPrefix = primaryRole.OnetCode.Split('-')[0];
                }

                bool isTech = DomainClassifier.IsTechDomain(primaryRole?.OnetCode, primaryRole?.Title);

                int skillOffset = roleTitles.Count;
                for (int i = 0; i < skillNames.Count; i++)
                {
                    var vector = vectors[skillOffset + i];

                    var domainMatch = await _context.RoleSkills
                        .Include(rs => rs.Skill)
                        .Include(rs => rs.Role)
                        .Where(rs => rs.Role.OnetCode != null && rs.Role.OnetCode.StartsWith(domainPrefix ?? "NONE"))
                        .Where(rs => rs.Skill.Embedding != null)
                        .Select(rs => new { rs.Skill.Name, Distance = rs.Skill.Embedding!.CosineDistance(vector) })
                        .OrderBy(x => x.Distance)
                        .FirstOrDefaultAsync();

                    if (domainMatch != null && domainMatch.Distance < 0.35)
                    {
                        signal.Skills[i].Name = domainMatch.Name;
                    }
                    else
                    {
                        var query = _context.Skills.Where(s => s.Embedding != null);
                        if (!isTech)
                            query = query.Where(s => s.Source != "Roadmap.sh");

                        var generalMatch = await query
                            .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(vector) })
                            .OrderBy(x => x.Distance)
                            .FirstOrDefaultAsync();

                        if (generalMatch != null)
                        {
                            signal.Skills[i].Name = generalMatch.Name;
                        }
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

        private static string SanitizePdfText(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (c == '\uF0B7' || c == '\uF0A7' || c == '\uF076')
                    sb.Append('-'); 
                else if (c >= '\uE000' && c <= '\uF8FF')
                    sb.Append(' '); 
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private string StripReferences(string text)
        {
            try
            {
                var pattern = @"(?i)\bREFERENCES\b.*$";
                var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Singleline);

                if (regex.IsMatch(text))
                {
                    _logger.LogInformation("Noise Reduction: Stripped 'REFERENCES' section from text.");
                    return regex.Replace(text, "");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to strip references section. Using full text.");
            }
            return text;
        }

        public async Task<List<TailoredBullet>?> TailorResumeAsync(
            Guid userProfileId,
            Guid jobId,
            List<string>? matchingSkills = null,
            List<string>? implicitSkills = null,
            List<string>? prereqMetSkills = null,
            List<string>? bridgeableSkills = null,
            List<string>? hardGaps = null)
        {
            var user = await _context.UserProfiles.FindAsync(userProfileId);
            var job = await _context.JobPostings.FindAsync(jobId);

            if (user?.CleanSignal == null || job?.CleanSignal == null)
                return null;

            var experienceEntries = user.CleanSignal.ExperienceSummary
                .Where(e => e.Bullets.Any(b => !string.IsNullOrWhiteSpace(b)))
                .ToList();

            if (experienceEntries.Count == 0)
            {
                _logger.LogWarning("TailorResume: User {UserId} has no experience bullets to tailor.", userProfileId);
                return [];
            }

            List<string> effectiveMatching, effectiveImplicit, effectivePrereqMet, effectiveBridgeable, effectiveHardGaps;
            if (matchingSkills != null)
            {
                effectiveMatching = matchingSkills;
                effectiveImplicit = implicitSkills ?? [];
                effectivePrereqMet = prereqMetSkills ?? [];
                effectiveBridgeable = bridgeableSkills ?? [];
                effectiveHardGaps = hardGaps ?? [];
            }
            else
            {
                var userSkillNames = user.CleanSignal.Skills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                effectiveMatching = [.. user.CleanSignal.Skills.Select(s => s.Name)];
                effectiveImplicit = [];
                effectivePrereqMet = [];
                effectiveBridgeable = [];
                effectiveHardGaps = [.. job.CleanSignal.RequiredSkills
                    .Where(js => !userSkillNames.Contains(js.Name))
                    .Select(js => js.Name)];
            }

            var jobTitle = job.CleanSignal.TargetRoles.FirstOrDefault()?.Title ?? "the role";
            var results = new List<TailoredBullet>();

            foreach (var exp in experienceEntries)
            {
                var bulletsText = string.Join("\n", exp.Bullets.Select((b, i) => $"{i + 1}. {b}"));

                var prompt = $$"""
                    You are an expert resume writer. Below is a candidate's experience entry and the full skill analysis for the job they are targeting.

                    ORIGINAL EXPERIENCE:
                    Role: {{exp.Role}}  |  Company: {{exp.Company}}
                    {{bulletsText}}

                    JOB TARGET: {{jobTitle}}

                    JOB REQUIRES — MATCHING SKILLS (candidate already has):
                    {{(effectiveMatching.Count > 0 ? string.Join(", ", effectiveMatching) : "(none)")}}

                    JOB REQUIRES — CAN BE INFERRED FROM BACKGROUND (implicit):
                    {{(effectiveImplicit.Count > 0 ? string.Join(", ", effectiveImplicit) : "(none)")}}

                    JOB REQUIRES — CANDIDATE HAS FOUNDATIONS FOR (prerequisite-met):
                    {{(effectivePrereqMet.Count > 0 ? string.Join(", ", effectivePrereqMet) : "(none)")}}

                    JOB REQUIRES — REACHABLE WITH EXISTING EXPERIENCE (bridgeable):
                    {{(effectiveBridgeable.Count > 0 ? string.Join(", ", effectiveBridgeable) : "(none)")}}

                    JOB REQUIRES — GENUINE GAPS (do NOT force these in):
                    {{(effectiveHardGaps.Count > 0 ? string.Join(", ", effectiveHardGaps) : "(none)")}}

                    TASK:
                    Rewrite each numbered bullet so it naturally incorporates relevant keywords from the matching, implicit, and bridgeable skill lists where they genuinely apply to what was done in this role.
                    Keep the original facts, company context, and achievements — do not invent new responsibilities.
                    Do not force bridgeable or gap skills into bullets where they do not fit.
                    Keep bullets concise (1-2 lines), action-verb-led, and quantified where the original was quantified.

                    Return JSON array only, no other text:
                    [{"original": "exact original bullet text", "rewritten": "rewritten bullet text"}]
                    """;

                try
                {
                    var response = await _chatClient.GetResponseAsync(prompt);
                    var text = response?.Text?.Trim() ?? "";

                    var json = System.Text.RegularExpressions.Regex.Replace(text, @"```(?:json)?", "").Trim();
                    var startIdx = json.IndexOf('[');
                    var endIdx   = json.LastIndexOf(']');
                    if (startIdx >= 0 && endIdx > startIdx)
                        json = json[startIdx..(endIdx + 1)];

                    var parsed = JsonSerializer.Deserialize<List<BulletRewriteItem>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (parsed != null)
                    {
                        foreach (var item in parsed)
                        {
                            if (!string.IsNullOrWhiteSpace(item.Original) && !string.IsNullOrWhiteSpace(item.Rewritten))
                            {
                                results.Add(new TailoredBullet
                                {
                                    OriginalBullet = item.Original,
                                    RewrittenBullet = item.Rewritten,
                                    TargetSkill = exp.Role,
                                    Role = exp.Role,
                                    Company = exp.Company,
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to tailor bullets for role {Role}", exp.Role);
                    foreach (var bullet in exp.Bullets.Where(b => !string.IsNullOrWhiteSpace(b)))
                    {
                        results.Add(new TailoredBullet
                        {
                            OriginalBullet = bullet,
                            RewrittenBullet = bullet,
                            TargetSkill = exp.Role,
                            Role = exp.Role,
                            Company = exp.Company,
                        });
                    }
                }
            }

            return results;
        }

        private record BulletRewriteItem(string Original, string Rewritten);
    }
}