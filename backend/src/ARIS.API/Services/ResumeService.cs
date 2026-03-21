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

        public ResumeService(ArisDbContext context, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IChatClient chatClient, PersonalInfoExtractor personalInfoExtractor, MatchService matchService, GraphService graphService, string ollamaGenerateUrl, string extractionModel, ILogger<ResumeService> logger, double firstPassThreshold = 0.15, double secondPassThreshold = 0.20, int extractionNumCtx = 4096)
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

        public async Task<float[]> GenerateEmbeddingAsync(string text)
        {
            var truncated = text.Length > 2000 ? text[..2000] : text;
            var embeddings = await _embeddingGenerator.GenerateAsync([truncated]);
            return embeddings[0].Vector.ToArray();
        }

        /// <summary>
        /// Extracts and grounds a <see cref="ResumeCleanSignal"/> from raw resume text entirely in memory.
        /// No database writes occur. Returns null if extraction fails.
        /// Also generates and returns the symmetric embedding vector for cosine similarity.
        /// </summary>
        public async Task<(ResumeCleanSignal? Signal, Pgvector.Vector? Embedding)> QuickExtractResumeSignalAsync(string rawText)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    _logger.LogWarning("QuickExtractResumeSignalAsync: raw text is empty.");
                    return (null, null);
                }

                rawText = StripReferences(rawText);
                rawText = SanitizePdfText(rawText);

                var cleanSignal = await ExtractCleanSignalAsync(rawText);
                if (cleanSignal == null)
                {
                    _logger.LogWarning("QuickExtractResumeSignalAsync: Clean Signal extraction returned null.");
                    return (null, null);
                }

                // Ground against the reference dictionary (mutates signal in memory only — no DB write)
                await GroundCleanSignalAsync(cleanSignal, "study-ephemeral");

                var symmetricString = BuildSymmetricString(cleanSignal);
                var truncated = symmetricString.Length > 2000 ? symmetricString[..2000] : symmetricString;
                var embeddings = await _embeddingGenerator.GenerateAsync([truncated]);
                var vector = new Pgvector.Vector(embeddings[0].Vector);

                return (cleanSignal, vector);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QuickExtractResumeSignalAsync failed.");
                return (null, null);
            }
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
                exp.StartDate = (exp.StartDate ?? "").Trim();
                exp.EndDate = (exp.EndDate ?? "").Trim();
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
                    ["from"] = "start_date",
                    ["begin_date"] = "start_date",
                    ["to"] = "end_date",
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

                    // Exact name match — handles acronyms (SQL, CSS, AWS, etc.) that may be
                    // filtered out of the Roadmap.sh-restricted general query for non-tech domains.
                    var skillNameLower = originalSkill.Name.ToLowerInvariant();
                    var exactMatch = await _context.Skills
                        .Where(s => s.Name.ToLower() == skillNameLower)
                        .FirstOrDefaultAsync();

                    if (exactMatch != null)
                    {
                        _logger.LogInformation("Skill '{Skill}' grounded via exact name match: '{Canonical}'.",
                            originalSkill.Name, exactMatch.Name);
                        groundedSkills.Add(new ResumeSkill
                        {
                            Name = exactMatch.Name,
                            OriginalName = string.Equals(originalSkill.Name, exactMatch.Name, StringComparison.OrdinalIgnoreCase) ? null : originalSkill.Name,
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

        public async Task<TailoredResumeData?> BuildTailoredResumeDataAsync(
            Guid userProfileId,
            Guid jobId,
            MatchAnalysisResult? precomputedMatch = null,
            IChatClient? llmClient = null)
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

            var graphContextBlock = _graphService.BuildTailoringGraphContext(match, user.CleanSignal?.Skills);
            var rawResumeText = ExtractRawResumeText(user.RawResume) ?? "";
            var rawJobText = job.RawDescription ?? "";

            var personalInfoTask = _personalInfoExtractor.ExtractAsync(rawResumeText, llmClient);

            var tailoringPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "ResumeTailoring.md");
            var tailoringTemplate = await File.ReadAllTextAsync(tailoringPromptPath);

            var prompt = tailoringTemplate
                .Replace("{rawResumeText}", rawResumeText)
                .Replace("{rawJobText}", rawJobText)
                .Replace("{graphContext}", graphContextBlock);

            string fullText;
            try
            {
                var options = new ChatOptions { Temperature = 0.2f };
                var response = await (llmClient ?? _chatClient).GetResponseAsync(prompt, options);
                fullText = response?.Text?.Trim() ?? "";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tailoring LLM call failed; falling back to original bullets.");
                fullText = "";
            }

            var (summary, bulletResults) = ParseTailoredOutput(fullText, experienceEntries);

            await personalInfoTask;
            return new TailoredResumeData(user.CleanSignal, personalInfoTask.Result, summary, bulletResults);
        }

        private (string Summary, List<TailoredBullet> Bullets) ParseTailoredOutput(
            string fullText,
            List<ExperienceSummary> experienceEntries)
        {
            var bulletResults = new List<TailoredBullet>();
            var summary = "";

            if (string.IsNullOrWhiteSpace(fullText))
            {
                foreach (var exp in experienceEntries)
                    foreach (var bullet in exp.Bullets.Where(b => !string.IsNullOrWhiteSpace(b)))
                        bulletResults.Add(new TailoredBullet
                        {
                            OriginalBullet = bullet,
                            RewrittenBullet = bullet,
                            TargetSkill = exp.Role,
                            Role = exp.Role,
                            Company = exp.Company,
                        });
                return (summary, bulletResults);
            }

            var lines = fullText.Split('\n');
            int idx = 0;

            for (; idx < lines.Length; idx++)
            {
                if (lines[idx].Trim().Equals("SUMMARY", StringComparison.OrdinalIgnoreCase))
                {
                    idx++;
                    break;
                }
            }

            if (idx >= lines.Length)
                idx = 0;

            var summaryLines = new List<string>();
            for (; idx < lines.Length; idx++)
            {
                var trimmed = lines[idx].Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    if (summaryLines.Count > 0) break;
                    continue;
                }
                if (trimmed.StartsWith("SKILLS", StringComparison.OrdinalIgnoreCase))
                    break;
                if (trimmed.Contains('|') || trimmed.Contains(" at ", StringComparison.OrdinalIgnoreCase))
                    break;
                summaryLines.Add(trimmed);
            }
            summary = string.Join(" ", summaryLines).Trim();

            string? currentRole = null;
            string? currentCompany = null;
            var currentBullets = new List<string>();

            void FlushEntry()
            {
                if (currentRole == null) return;
                var matchedExp = experienceEntries.FirstOrDefault(e =>
                    currentRole.Contains(e.Role, StringComparison.OrdinalIgnoreCase) ||
                    e.Role.Contains(currentRole, StringComparison.OrdinalIgnoreCase));
                var role = matchedExp?.Role ?? currentRole;
                var company = matchedExp?.Company ?? currentCompany ?? "";
                var originalBullets = matchedExp?.Bullets ?? [];

                for (int b = 0; b < currentBullets.Count; b++)
                {
                    var origBullet = b < originalBullets.Count ? originalBullets[b] : "";
                    bulletResults.Add(new TailoredBullet
                    {
                        OriginalBullet = origBullet,
                        RewrittenBullet = currentBullets[b],
                        TargetSkill = role,
                        Role = role,
                        Company = company,
                    });
                }
                currentRole = null;
                currentCompany = null;
                currentBullets.Clear();
            }

            for (; idx < lines.Length; idx++)
            {
                var line = lines[idx].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.Contains('|'))
                {
                    FlushEntry();
                    var parts = line.Split('|');
                    currentRole = parts[0].Trim();
                    currentCompany = parts.Length > 1 ? parts[1].Trim() : "";
                    continue;
                }

                var isHeader = !string.IsNullOrWhiteSpace(line)
                    && !line.StartsWith('-')
                    && line.Contains(" at ", StringComparison.OrdinalIgnoreCase)
                    && line.Length < 120;
                if (isHeader)
                {
                    FlushEntry();
                    var atIdx = line.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);
                    currentRole = line[..atIdx].Trim();
                    currentCompany = line[(atIdx + 4)..].Trim();
                    continue;
                }

                if (currentRole != null)
                {
                    var bulletText = line.TrimStart('-', ' ');
                    if (bulletText.Length > 200 && bulletText.Contains(". "))
                    {
                        var sentences = System.Text.RegularExpressions.Regex.Split(bulletText, @"(?<=\.)\s+");
                        foreach (var s in sentences)
                        {
                            var trimmed = s.Trim();
                            if (!string.IsNullOrWhiteSpace(trimmed))
                                currentBullets.Add(trimmed);
                        }
                    }
                    else
                    {
                        currentBullets.Add(bulletText);
                    }
                }
            }

            FlushEntry();

            if (bulletResults.Count == 0)
            {
                foreach (var exp in experienceEntries)
                    foreach (var bullet in exp.Bullets.Where(b => !string.IsNullOrWhiteSpace(b)))
                        bulletResults.Add(new TailoredBullet
                        {
                            OriginalBullet = bullet,
                            RewrittenBullet = bullet,
                            TargetSkill = exp.Role,
                            Role = exp.Role,
                            Company = exp.Company,
                        });
            }

            return (summary, bulletResults);
        }

    }
}