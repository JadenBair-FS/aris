using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using ARIS.Shared.Helpers;
using ARIS.Shared.Models;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UglyToad.PdfPig;
using Pgvector;
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
        private readonly string _ollamaGenerateUrl;
        private readonly string _extractionModel;
        private readonly int _extractionNumCtx;
        private readonly double _firstPassThreshold;
        private readonly double _secondPassThreshold;
        private readonly PersonalInfoExtractor _personalInfoExtractor;
        private readonly MatchService _matchService;
        private readonly GraphService _graphService;
        private readonly ILogger<ResumeService> _logger;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromHours(1) };

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Converters = { new LenientStringConverter(), new LenientDoubleConverter() }
        };

        public ResumeService(ArisDbContext context, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IChatClient chatClient, PersonalInfoExtractor personalInfoExtractor, MatchService matchService, GraphService graphService, string ollamaGenerateUrl, string extractionModel, ILogger<ResumeService> logger, double firstPassThreshold = 0.10, double secondPassThreshold = 0.35, int extractionNumCtx = 4096)
        {
            _context = context;
            _embeddingGenerator = embeddingGenerator;
            _chatClient = chatClient;
            _personalInfoExtractor = personalInfoExtractor;
            _matchService = matchService;
            _graphService = graphService;
            _ollamaGenerateUrl = ollamaGenerateUrl;
            _extractionModel = extractionModel;
            _extractionNumCtx = extractionNumCtx;
            _firstPassThreshold = firstPassThreshold;
            _secondPassThreshold = secondPassThreshold;
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

                await GroundCleanSignalAsync(cleanSignal, userId);

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


        private async Task<ResumeCleanSignal?> ExtractCleanSignalAsync(string rawText)
        {
            string userPrompt;
            try
            {
                var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "ResumeExtraction.md");
                var template = await File.ReadAllTextAsync(promptPath);

                var truncatedText = rawText.Length > 6000 ? rawText[..6000] : rawText;

                userPrompt = template.Replace("{raw_text}", truncatedText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load prompt template.");
                return null;
            }

            try
            {
                var requestBody = new { model = _extractionModel, prompt = userPrompt, stream = false, format = "json", options = new { num_ctx = _extractionNumCtx } };
                using var httpResponse = await _http.PostAsJsonAsync(_ollamaGenerateUrl, requestBody);
                httpResponse.EnsureSuccessStatusCode();
                var result = await httpResponse.Content.ReadFromJsonAsync<JsonElement>();
                var jsonString = result.GetProperty("response").GetString()?.Trim();

                if (string.IsNullOrWhiteSpace(jsonString))
                {
                    _logger.LogWarning("NuExtract returned empty response for resume.");
                    return null;
                }

                _logger.LogInformation("NuExtract resume response: {Json}", jsonString);

                jsonString = UnwrapIfNeeded(jsonString);
                jsonString = NormalizeFieldNames(jsonString);

                var signal = JsonSerializer.Deserialize<ResumeCleanSignal>(jsonString, _jsonOptions);
                if (signal == null)
                {
                    _logger.LogWarning("Resume deserialization returned null.");
                    return null;
                }

                PostProcessCleanSignal(signal);

                var validation = ValidateCleanSignal(signal);
                if (validation != null)
                    _logger.LogWarning("Resume validation warning: {Reason}", validation);

                return signal;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NuExtract resume extraction failed.");
                return null;
            }
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

        private async Task GroundCleanSignalAsync(ResumeCleanSignal signal, string sourceDocId)
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
                var groundedSkills = new List<ResumeSkill>();
                var ungroundedSkills = new List<ResumeSkill>();

                for (int i = 0; i < skillNames.Count; i++)
                {
                    var vector = vectors[skillOffset + i];
                    var originalSkill = signal.Skills[i];

                    var domainMatch = await _context.RoleSkills
                        .Include(rs => rs.Skill)
                        .Include(rs => rs.Role)
                        .Where(rs => rs.Role.OnetCode != null && rs.Role.OnetCode.StartsWith(domainPrefix ?? "NONE"))
                        .Where(rs => rs.Skill.Embedding != null)
                        .Select(rs => new { rs.Skill.Name, Distance = rs.Skill.Embedding!.CosineDistance(vector) })
                        .OrderBy(x => x.Distance)
                        .FirstOrDefaultAsync();

                    if (domainMatch != null && domainMatch.Distance < _firstPassThreshold)
                    {
                        groundedSkills.Add(new ResumeSkill
                        {
                            Name = domainMatch.Name,
                            OriginalName = string.Equals(originalSkill.Name, domainMatch.Name, StringComparison.OrdinalIgnoreCase) ? null : originalSkill.Name,
                            Category = originalSkill.Category,
                            Proficiency = originalSkill.Proficiency,
                            YearsOfExperience = originalSkill.YearsOfExperience
                        });
                        continue;
                    }

                    var generalQuery = _context.Skills.Where(s => s.Embedding != null);
                    if (!isTech)
                        generalQuery = generalQuery.Where(s => s.Source != "Roadmap.sh");

                    var generalMatch = await generalQuery
                        .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(vector) })
                        .OrderBy(x => x.Distance)
                        .FirstOrDefaultAsync();

                    if (generalMatch != null && generalMatch.Distance < _firstPassThreshold)
                    {
                        groundedSkills.Add(new ResumeSkill
                        {
                            Name = generalMatch.Name,
                            OriginalName = string.Equals(originalSkill.Name, generalMatch.Name, StringComparison.OrdinalIgnoreCase) ? null : originalSkill.Name,
                            Category = originalSkill.Category,
                            Proficiency = originalSkill.Proficiency,
                            YearsOfExperience = originalSkill.YearsOfExperience
                        });
                        continue;
                    }

                    bool isAcronym = originalSkill.Name.Length <= 6
                        && !originalSkill.Name.Contains(' ')
                        && originalSkill.Name == originalSkill.Name.ToUpperInvariant();
                    if (!isAcronym)
                    {
                        var secondPassMatch = await _context.Skills
                            .Where(s => s.Embedding != null)
                            .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(vector) })
                            .OrderBy(x => x.Distance)
                            .FirstOrDefaultAsync();

                        if (secondPassMatch != null && secondPassMatch.Distance < _secondPassThreshold)
                        {
                            var origNorm = originalSkill.Name.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace(".", "");
                            var canonNorm = secondPassMatch.Name.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace(".", "");
                            bool substringRelated = origNorm.Contains(canonNorm) || canonNorm.Contains(origNorm);

                            if (substringRelated)
                            {
                                _logger.LogInformation("Skill '{Skill}' grounded via pass 2: '{Canonical}' ({Distance:F3}).",
                                    originalSkill.Name, secondPassMatch.Name, secondPassMatch.Distance);
                                groundedSkills.Add(new ResumeSkill
                                {
                                    Name = secondPassMatch.Name,
                                    OriginalName = string.Equals(originalSkill.Name, secondPassMatch.Name, StringComparison.OrdinalIgnoreCase) ? null : originalSkill.Name,
                                    Category = originalSkill.Category,
                                    Proficiency = originalSkill.Proficiency,
                                    YearsOfExperience = originalSkill.YearsOfExperience
                                });
                                continue;
                            }
                            else
                            {
                                _logger.LogInformation("Skill '{Skill}' second-pass candidate '{Canonical}' rejected (no substring relation).",
                                    originalSkill.Name, secondPassMatch.Name);
                            }
                        }
                    }

                    _logger.LogInformation("Skill '{Skill}' not on graph — best distance: {Dist:F3}.",
                        originalSkill.Name, generalMatch?.Distance ?? 1.0);
                    ungroundedSkills.Add(originalSkill);
                }

                signal.Skills = groundedSkills
                    .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new ResumeSkill
                    {
                        Name = g.First().Name,
                        OriginalName = g.Count() == 1 ? g.First().OriginalName : null,
                        Category = g.First().Category,
                        Proficiency = g.First().Proficiency,
                        YearsOfExperience = g.Sum(s => s.YearsOfExperience)
                    })
                    .ToList();

                signal.UngroundedSkills = ungroundedSkills
                    .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new ResumeSkill
                    {
                        Name = g.First().Name,
                        Category = g.First().Category,
                        Proficiency = g.First().Proficiency,
                        YearsOfExperience = g.Sum(s => s.YearsOfExperience)
                    })
                    .ToList();

                _logger.LogInformation("Grounding complete: {Grounded} canonical, {Ungrounded} ungrounded.",
                    signal.Skills.Count, signal.UngroundedSkills.Count);
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

        public async Task<ResumeCleanSignal?> ApplyGroundingCorrectionsAsync(Guid profileId, List<GroundingCorrectionItem> corrections)
        {
            var profile = await _context.UserProfiles.FindAsync(profileId);
            if (profile?.CleanSignal == null) return null;

            var cs = profile.CleanSignal;

            foreach (var correction in corrections)
            {
                var ungroundedMatch = cs.UngroundedSkills.FirstOrDefault(s =>
                    string.Equals(s.Name, correction.From, StringComparison.OrdinalIgnoreCase));
                if (ungroundedMatch != null)
                {
                    cs.UngroundedSkills.Remove(ungroundedMatch);
                    if (!cs.Skills.Any(s => string.Equals(s.Name, correction.To, StringComparison.OrdinalIgnoreCase)))
                    {
                        cs.Skills.Add(new ResumeSkill
                        {
                            Name = correction.To,
                            OriginalName = ungroundedMatch.Name,
                            Category = ungroundedMatch.Category,
                            Proficiency = ungroundedMatch.Proficiency,
                            YearsOfExperience = ungroundedMatch.YearsOfExperience
                        });
                    }
                    continue;
                }
                var groundedMatch = cs.Skills.FirstOrDefault(s =>
                    s.OriginalName != null &&
                    string.Equals(s.OriginalName, correction.From, StringComparison.OrdinalIgnoreCase));
                if (groundedMatch != null)
                {
                    if (string.IsNullOrEmpty(correction.To))
                    {
                        cs.Skills.Remove(groundedMatch);
                        if (!cs.UngroundedSkills.Any(s => string.Equals(s.Name, groundedMatch.OriginalName, StringComparison.OrdinalIgnoreCase)))
                        {
                            cs.UngroundedSkills.Add(new ResumeSkill
                            {
                                Name = groundedMatch.OriginalName!,
                                Category = groundedMatch.Category,
                                Proficiency = groundedMatch.Proficiency,
                                YearsOfExperience = groundedMatch.YearsOfExperience
                            });
                        }
                    }
                    else
                    {
                        groundedMatch.Name = correction.To;
                    }
                }
            }

            var symStr = BuildSymmetricString(cs);
            var truncated = symStr.Length > 2000 ? symStr[..2000] : symStr;
            var emb = await _embeddingGenerator.GenerateAsync([truncated]);
            profile.Embedding = new Vector(emb[0].Vector);
            profile.UpdatedAt = DateTime.UtcNow;

            _context.Entry(profile).State = EntityState.Modified;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Applied {Count} grounding corrections to profile {ProfileId}.", corrections.Count, profileId);
            return cs;
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

                    Use plain text only — do not use markdown, asterisks, bold, italic, or any special formatting in the rewritten bullets.

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

                    var parsed = JsonSerializer.Deserialize<List<BulletRewriteItem>>(json, _jsonOptions);

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

        public record TailoredResumeData(
            ResumeCleanSignal CleanSignal,
            PersonalInfo PersonalInfo,
            string ProfessionalSummary,
            List<TailoredBullet> TailoredBullets);

        private static string? ExtractRawResumeText(string? rawResumeJson)
        {
            if (string.IsNullOrWhiteSpace(rawResumeJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(rawResumeJson);
                if (doc.RootElement.TryGetProperty("content", out var contentProp))
                    return contentProp.GetString();
            }
            catch { }
            return rawResumeJson;
        }

        private async Task<string> GenerateSummaryAsync(
            string rawText,
            string jobTitle,
            List<string> matchingSkills,
            List<string> implicitSkills,
            List<string> bridgeableSkills,
            List<string> hardGaps,
            string rawJobDescription = "")
        {
            var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "ResumeSummary.md");
            var template = await File.ReadAllTextAsync(promptPath);
            var snippet = rawText.Length > 3000 ? rawText[..3000] : rawText;
            var jobSnippet = rawJobDescription.Length > 1500 ? rawJobDescription[..1500] : rawJobDescription;

            var prompt = template
                .Replace("{rawResumeSnippet}", snippet)
                .Replace("{rawJobSnippet}", jobSnippet)
                .Replace("{jobTitle}", jobTitle)
                .Replace("{matchingSkills}", matchingSkills.Count > 0 ? string.Join(", ", matchingSkills) : "(none)")
                .Replace("{implicitSkills}", implicitSkills.Count > 0 ? string.Join(", ", implicitSkills) : "(none)")
                .Replace("{bridgeableSkills}", bridgeableSkills.Count > 0 ? string.Join(", ", bridgeableSkills) : "(none)")
                .Replace("{hardGaps}", hardGaps.Count > 0 ? string.Join(", ", hardGaps) : "(none)");

            try
            {
                var response = await _chatClient.GetResponseAsync(prompt);
                return response?.Text?.Trim() ?? "";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate professional summary for job {JobTitle}.", jobTitle);
                return "";
            }
        }

        public async Task<TailoredResumeData?> BuildTailoredResumeDataAsync(
            Guid userProfileId,
            Guid jobId,
            MatchAnalysisResult? precomputedMatch = null)
        {
            var user = await _context.UserProfiles.FindAsync(userProfileId);
            var job = await _context.JobPostings.FindAsync(jobId);

            if (user?.CleanSignal == null || job?.CleanSignal == null)
                return null;

            var experienceEntries = user.CleanSignal.ExperienceSummary
                .Where(e => e.Bullets.Any(b => !string.IsNullOrWhiteSpace(b)))
                .ToList();

            var match = precomputedMatch ?? await _matchService.AnalyzeMatchAsync(userProfileId, jobId);
            if (match == null)
                return null;

            var effectiveMatching = match.MatchingSkills.Select(s => s.SkillName).ToList();
            var effectiveImplicit = match.ImplicitlyDiscoveredSkills.ToList();
            var effectiveBridgeable = match.BridgeableSkills.Select(s => s.SkillName).ToList();
            var effectiveHardGaps = match.HardGaps.Select(s => s.SkillName).ToList();

            var graphContextBlock = _graphService.BuildTailoringGraphContext(match, user.CleanSignal?.Skills);
            var jobTitle = job.CleanSignal.TargetRoles.FirstOrDefault()?.Title ?? "the role";
            var rawResumeText = ExtractRawResumeText(user.RawResume) ?? "";
            var rawJobText = job.RawDescription ?? "";

            var personalInfoTask = _personalInfoExtractor.ExtractAsync(rawResumeText);
            var summaryTask = GenerateSummaryAsync(
                rawResumeText, jobTitle,
                effectiveMatching, effectiveImplicit, effectiveBridgeable, effectiveHardGaps,
                rawJobText);

            var bulletResults = new List<TailoredBullet>();
            foreach (var exp in experienceEntries)
            {
                var bulletsText = string.Join("\n", exp.Bullets.Select((b, i) => $"{i + 1}. {b}"));

                var prompt = $$"""
                    You are an expert resume writer with access to a validated knowledge graph.

                    FULL RESUME (raw text — use for facts, voice, and context):
                    {{rawResumeText}}

                    FULL JOB DESCRIPTION (raw text — use employer's own language):
                    {{rawJobText}}

                    {{graphContextBlock}}

                    EXPERIENCE ENTRY TO REWRITE:
                    Role: {{exp.Role}} | Company: {{exp.Company}}
                    {{bulletsText}}

                    TASK:
                    Rewrite each bullet to read as polished, professional resume content. Follow these rules:
                    1. Every skill listed under CLAIM DIRECTLY, HEDGE NATURALLY, and TRANSFER NATURALLY above MUST appear by name in at least one rewritten bullet. Each skill MUST appear in a SEPARATE bullet — do not put multiple graph skills into the same bullet, and do not skip any.
                    2. Use the exact skill name shown in the list above (the job description's own terminology). Never substitute a canonical variant (write "React" not "React.js", "Postgres" not "PostgreSQL").
                    3. For CLAIM DIRECTLY skills: state proficiency outright. No hedging.
                    4. For HEDGE NATURALLY skills: use natural framing like the example given — e.g., "applies X knowledge to develop Y proficiency". Never claim direct experience you do not have.
                    5. For TRANSFER NATURALLY skills: use transfer framing like the example given — e.g., "draws on X experience to work effectively with Y".
                    6. Never mention any skill listed under DO NOT MENTION.
                    7. Every claim must connect to a real fact in the original resume. Do not invent projects or responsibilities.
                    8. Keep bullets concise (1-2 lines), action-verb-led, quantified where the original was quantified.
                    9. Plain text only — no markdown, asterisks, bold, italic, bullet symbols, or any special formatting.

                    Return JSON array only: [{"original": "exact original bullet text", "rewritten": "rewritten bullet text"}]
                    """;

                try
                {
                    var response = await _chatClient.GetResponseAsync(prompt);
                    var text = response?.Text?.Trim() ?? "";
                    var json = System.Text.RegularExpressions.Regex.Replace(text, @"```(?:json)?", "").Trim();
                    var startIdx = json.IndexOf('[');
                    var endIdx = json.LastIndexOf(']');
                    if (startIdx >= 0 && endIdx > startIdx)
                        json = json[startIdx..(endIdx + 1)];

                    var parsed = JsonSerializer.Deserialize<List<BulletRewriteItem>>(json, _jsonOptions);

                    if (parsed != null)
                    {
                        foreach (var item in parsed)
                        {
                            if (!string.IsNullOrWhiteSpace(item.Original) && !string.IsNullOrWhiteSpace(item.Rewritten))
                            {
                                bulletResults.Add(new TailoredBullet
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
                        bulletResults.Add(new TailoredBullet
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

            await Task.WhenAll(personalInfoTask, summaryTask);
            return new TailoredResumeData(user.CleanSignal, personalInfoTask.Result, summaryTask.Result, bulletResults);
        }
    }
}