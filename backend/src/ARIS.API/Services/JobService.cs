using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using ARIS.Shared.Helpers;
using ARIS.Shared.Models;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.Extensions.AI;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        private readonly string _ollamaGenerateUrl;
        private readonly string _extractionModel;
        private readonly int _extractionNumCtx;
        private readonly double _firstPassThreshold;
        private readonly double _secondPassThreshold;
        private readonly ILogger<JobService> _logger;

        private static readonly HttpClient _extractionHttp = new() { Timeout = TimeSpan.FromHours(1) };

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Converters = { new LenientStringConverter(), new LenientDoubleConverter() }
        };

        public JobService(ArisDbContext context, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IChatClient chatClient, string ollamaGenerateUrl, string extractionModel, ILogger<JobService> logger, double firstPassThreshold = 0.15, double secondPassThreshold = 0.20, int extractionNumCtx = 4096)
        {
            _context = context;
            _embeddingGenerator = embeddingGenerator;
            _chatClient = chatClient;
            _ollamaGenerateUrl = ollamaGenerateUrl;
            _extractionModel = extractionModel;
            _extractionNumCtx = extractionNumCtx;
            _firstPassThreshold = firstPassThreshold;
            _secondPassThreshold = secondPassThreshold;
            _logger = logger;
        }

        public async Task<Guid?> CreateJobPostingAsync(string rawDescription, string recruiterId, string? sourceUrl = null)
        {
            try
            {
                var cleanSignal = await ExtractJobCleanSignalAsync(rawDescription);
                if (cleanSignal == null)
                {
                    _logger.LogWarning("Failed to extract Clean Signal for job posting by {RecruiterId}", recruiterId);
                    return null;
                }

                if (cleanSignal.RequiredSkills.Count < 3 && cleanSignal.Responsibilities.Count > 0)
                {
                    _logger.LogInformation("Sparse skill extraction ({Count} skills). Running responsibilities-based fallback pass.", cleanSignal.RequiredSkills.Count);
                    var fallbackSkills = await ExtractSkillsFromResponsibilitiesAsync(cleanSignal.Responsibilities, cleanSignal.TargetRoles.FirstOrDefault()?.Title);
                    var existingNames = cleanSignal.RequiredSkills.Select(s => s.Name.ToLowerInvariant()).ToHashSet();
                    foreach (var skill in fallbackSkills)
                    {
                        if (!existingNames.Contains(skill.Name.ToLowerInvariant()))
                            cleanSignal.RequiredSkills.Add(skill);
                    }
                }

                var jobPostingId = Guid.NewGuid();
                await GroundCleanSignalAsync(cleanSignal, jobPostingId.ToString());

                var symmetricString = BuildSymmetricString(cleanSignal);
                var truncatedSymmetric = symmetricString.Length > 2000 ? symmetricString[..2000] : symmetricString;
                var embeddings = await _embeddingGenerator.GenerateAsync([truncatedSymmetric]);
                var vectorData = embeddings[0].Vector;

                var jobPosting = new JobPosting
                {
                    Id = jobPostingId,
                    RecruiterId = recruiterId,
                    RawDescription = rawDescription,
                    CleanSignal = cleanSignal,
                    Embedding = new Vector(vectorData),
                    SourceUrl = sourceUrl,
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

            var candidates = await _context.JobPostings
                .Where(j => j.Embedding != null)
                .Select(j => new
                {
                    Job = j,
                    Distance = j.Embedding!.CosineDistance(userProfile.Embedding)
                })
                .Where(x => x.Distance < 0.65)
                .OrderBy(x => x.Distance)
                .Take(20)
                .ToListAsync();

            if (candidates.Count == 0)
            {
                return new JobRecommendationResponse
                {
                    Matches = [],
                    Analysis = "No matching jobs found in the database. Try updating your profile or searching for broader roles."
                };
            }

            var mappedMatches = candidates.Take(limit).Select(x => new JobMatchResult
            {
                JobId = x.Job.Id,
                Job = x.Job,
                Distance = x.Distance,
                Score = 1.0 - x.Distance,
                ArisScore = 0,
            }).ToList();

            var analysisText = await GenerateMatchAnalysisAsync(userProfile.CleanSignal, [.. mappedMatches.Take(3)]);

            return new JobRecommendationResponse
            {
                Matches = mappedMatches,
                Analysis = analysisText
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

        private async Task<List<JobSkill>> ExtractSkillsFromResponsibilitiesAsync(List<string> responsibilities, string? roleTitle)
        {
            try
            {
                var duties = string.Join("\n", responsibilities.Select((r, i) => $"{i + 1}. {r}"));

                var prompt = $$"""
                    The job posting for "{{roleTitle ?? "a professional role"}}" has these responsibilities:

                    {{duties}}

                    Extract one professional competency per responsibility line.
                    Return a JSON object with a single key "skills" whose value is an array.
                    Each array element must have exactly these three fields:
                      "name": a concise competency label of 1-5 words
                      "importance": "Essential"
                      "years_of_experience": 0.0
                    """;

                var chatOptions = new ChatOptions
                {
                    ResponseFormat = ChatResponseFormat.Json,
                    Temperature = 0.1f,
                };

                var response = await _chatClient.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, "You are a skill extraction engine. Output only valid JSON. No preamble, no markdown."),
                     new ChatMessage(ChatRole.User, prompt)],
                    chatOptions);

                var json = response?.Text?.Trim() ?? "";
                _logger.LogInformation("Responsibilities fallback raw LLM response: {Json}", json);

                if (json.TrimStart().StartsWith("{"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        string? extracted = null;

                        foreach (var key in new[] { "skills", "required_skills", "competencies", "extracted_skills" })
                        {
                            if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Array)
                            {
                                extracted = prop.GetRawText();
                                break;
                            }
                        }

                        if (extracted == null)
                        {
                            foreach (var prop in root.EnumerateObject())
                            {
                                if (prop.Value.ValueKind == JsonValueKind.Array)
                                {
                                    extracted = prop.Value.GetRawText();
                                    break;
                                }
                            }
                        }

                        if (extracted != null)
                            json = extracted;
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Fallback: could not parse wrapper object.");
                    }
                }

                if (!json.TrimStart().StartsWith("["))
                {
                    _logger.LogWarning("Responsibilities fallback: response is not a JSON array after unwrapping. Skipping.");
                    return [];
                }

                var skills = JsonSerializer.Deserialize<List<JobSkill>>(json, _jsonOptions) ?? [];

                skills.RemoveAll(s => string.IsNullOrWhiteSpace(s.Name));
                foreach (var s in skills) s.Name = s.Name.Trim();
                skills.RemoveAll(s => s.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 7);

                _logger.LogInformation("Responsibilities fallback extracted {Count} skills: {Names}",
                    skills.Count, string.Join(", ", skills.Select(s => s.Name)));

                return skills;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Responsibilities fallback extraction failed. Skipping.");
                return [];
            }
        }

        private async Task GroundCleanSignalAsync(JobPostingCleanSignal signal, string sourceDocId)
        {
            var roleTitles = signal.TargetRoles.Select(r => r.Title).ToList();
            var skillNames = signal.RequiredSkills.Select(s => s.Name).ToList();

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
                        signal.TargetRoles[i].OnetCode = match.OnetCode;
                    }
                }

                var primaryRole = signal.TargetRoles.FirstOrDefault(r => r.Priority == "Primary") ?? signal.TargetRoles.FirstOrDefault();
                string? domainPrefix = null;
                if (primaryRole?.OnetCode != null && primaryRole.OnetCode.Contains('-'))
                {
                    domainPrefix = primaryRole.OnetCode.Split('-')[0];
                }

                bool isTech = DomainClassifier.IsTechDomain(primaryRole?.OnetCode, primaryRole?.Title);

                int skillOffset = roleTitles.Count;
                var groundedSkills = new List<JobSkill>();
                var ungroundedSkills = new List<JobSkill>();

                for (int i = 0; i < skillNames.Count; i++)
                {
                    var vector = vectors[skillOffset + i];
                    var originalSkill = signal.RequiredSkills[i];

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
                        groundedSkills.Add(new JobSkill
                        {
                            Name = domainMatch.Name,
                            OriginalName = string.Equals(originalSkill.Name, domainMatch.Name, StringComparison.OrdinalIgnoreCase) ? null : originalSkill.Name,
                            Category = originalSkill.Category,
                            Importance = originalSkill.Importance,
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
                        groundedSkills.Add(new JobSkill
                        {
                            Name = generalMatch.Name,
                            OriginalName = string.Equals(originalSkill.Name, generalMatch.Name, StringComparison.OrdinalIgnoreCase) ? null : originalSkill.Name,
                            Category = originalSkill.Category,
                            Importance = originalSkill.Importance,
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
                        groundedSkills.Add(new JobSkill
                        {
                            Name = exactMatch.Name,
                            OriginalName = string.Equals(originalSkill.Name, exactMatch.Name, StringComparison.OrdinalIgnoreCase) ? null : originalSkill.Name,
                            Category = originalSkill.Category,
                            Importance = originalSkill.Importance,
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
                            groundedSkills.Add(new JobSkill
                            {
                                Name = secondPassMatch.Name,
                                OriginalName = string.Equals(originalSkill.Name, secondPassMatch.Name, StringComparison.OrdinalIgnoreCase) ? null : originalSkill.Name,
                                Category = originalSkill.Category,
                                Importance = originalSkill.Importance,
                                YearsOfExperience = originalSkill.YearsOfExperience
                            });
                            continue;
                        }
                    }

                    _logger.LogInformation("Skill '{Skill}' not on graph — best distance: {Dist:F3}.",
                        originalSkill.Name, generalMatch?.Distance ?? 1.0);
                    ungroundedSkills.Add(originalSkill);
                }

                signal.RequiredSkills = groundedSkills
                    .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new JobSkill
                    {
                        Name = g.First().Name,
                        OriginalName = g.Count() == 1 ? g.First().OriginalName : null,
                        Category = g.First().Category,
                        Importance = g.Any(s => s.Importance == "Essential") ? "Essential" : g.First().Importance,
                        YearsOfExperience = g.Sum(s => s.YearsOfExperience)
                    })
                    .ToList();

                signal.UngroundedSkills = ungroundedSkills
                    .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new JobSkill
                    {
                        Name = g.First().Name,
                        Category = g.First().Category,
                        Importance = g.Any(s => s.Importance == "Essential") ? "Essential" : g.First().Importance,
                        YearsOfExperience = g.Sum(s => s.YearsOfExperience)
                    })
                    .ToList();

                _logger.LogInformation("Grounding complete: {Grounded} canonical, {Ungrounded} ungrounded.",
                    signal.RequiredSkills.Count, signal.UngroundedSkills.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ground job signal. Proceeding with ungrounded data.");
            }
        }


        private async Task<JobPostingCleanSignal?> ExtractJobCleanSignalAsync(string rawText)
        {
            string userPrompt;
            try
            {
                var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "JobExtraction.md");
                var template = await File.ReadAllTextAsync(promptPath);

                var truncatedText = rawText.Length > 6000 ? rawText[..6000] : rawText;

                userPrompt = template.Replace("{raw_text}", truncatedText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load Job Extraction prompt.");
                return null;
            }

            try
            {
                var requestBody = new { model = _extractionModel, prompt = userPrompt, stream = false, format = "json", options = new { num_ctx = _extractionNumCtx } };
                using var httpResponse = await _extractionHttp.PostAsJsonAsync(_ollamaGenerateUrl, requestBody);
                httpResponse.EnsureSuccessStatusCode();
                var result = await httpResponse.Content.ReadFromJsonAsync<JsonElement>();
                var jsonString = result.GetProperty("response").GetString()?.Trim();

                if (string.IsNullOrWhiteSpace(jsonString))
                {
                    _logger.LogWarning("NuExtract returned empty response for job.");
                    return null;
                }

                _logger.LogInformation("NuExtract job response: {Json}", jsonString);

                jsonString = UnwrapIfNeeded(jsonString);
                jsonString = NormalizeJobFieldNames(jsonString);

                var signal = JsonSerializer.Deserialize<JobPostingCleanSignal>(jsonString, _jsonOptions);
                if (signal == null)
                {
                    _logger.LogWarning("Job deserialization returned null.");
                    return null;
                }

                PostProcessJobCleanSignal(signal);

                var validation = ValidateJobCleanSignal(signal);
                if (validation != null)
                    _logger.LogWarning("Job validation warning: {Reason}", validation);

                return signal;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NuExtract job extraction failed.");
                return null;
            }
        }


        private static string? ValidateJobCleanSignal(JobPostingCleanSignal signal)
        {
            if (signal.RequiredSkills.Count == 0)
                return "No required skills were extracted. Every job posting should have at least one technical skill requirement.";

            if (signal.TargetRoles.Count == 0)
                return "No target roles were identified.";

            return null;
        }

        private static void PostProcessJobCleanSignal(JobPostingCleanSignal signal)
        {
            signal.TargetRoles.RemoveAll(r => string.IsNullOrWhiteSpace(r.Title));
            foreach (var role in signal.TargetRoles) role.Title = (role.Title ?? "").Trim();

            signal.RequiredSkills.RemoveAll(s => string.IsNullOrWhiteSpace(s.Name));
            foreach (var skill in signal.RequiredSkills) skill.Name = (skill.Name ?? "").Trim();

            signal.RequiredSkills.RemoveAll(s => s.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 7);

            var eduDegrees = signal.MinimumEducation
                .Select(e => (e.Degree ?? "").Trim().ToLowerInvariant())
                .ToHashSet();
            signal.RequiredSkills.RemoveAll(s => eduDegrees.Contains(s.Name.ToLowerInvariant()));

            signal.Responsibilities.RemoveAll(string.IsNullOrWhiteSpace);

            signal.MinimumEducation.RemoveAll(e => string.IsNullOrWhiteSpace(e.Degree));
            foreach (var edu in signal.MinimumEducation) edu.Degree = (edu.Degree ?? "").Trim();
        }

        private static string NormalizeJobFieldNames(string jsonString)
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
                        var name = prop.Name.ToLower();
                        var targetName = prop.Name;

                        if (name == "roles" || name == "targetroles") targetName = "target_roles";
                        else if (name == "skills" || name == "requiredskills" || name == "qualifications") targetName = "required_skills";
                        else if (name == "education") targetName = "minimum_education";

                        writer.WritePropertyName(targetName);

                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            writer.WriteStartArray();
                            foreach (var item in prop.Value.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.Object)
                                {
                                    WriteNormalizedJobObject(writer, item, targetName);
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

        private static void WriteNormalizedJobObject(Utf8JsonWriter writer, JsonElement element, string arrayKey)
        {
            var renames = arrayKey switch
            {
                "target_roles" => new Dictionary<string, string> { ["name"] = "title" },
                "required_skills" => new Dictionary<string, string> 
                { 
                    ["yoe"] = "years_of_experience",
                    ["experience"] = "years_of_experience",
                    ["years"] = "years_of_experience",
                    ["duration"] = "years_of_experience"
                },
                _ => new Dictionary<string, string>()
            };

            writer.WriteStartObject();
            var written = new HashSet<string>();

            foreach (var prop in element.EnumerateObject())
            {
                var key = renames.TryGetValue(prop.Name.ToLower(), out var renamed) ? renamed : prop.Name;
                if (!written.Add(key)) continue;

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
                    if (inner.TryGetProperty("target_roles", out _) || inner.TryGetProperty("required_skills", out _))
                    {
                        return inner.GetRawText();
                    }
                }
            }
            catch { }

            return jsonString;
        }

        /// <summary>
        /// Extracts a <see cref="JobPostingCleanSignal"/> from raw text and grounds it against the
        /// reference dictionary, then generates an embedding — all in memory with no database writes.
        /// Used by the ephemeral quick-match endpoint.
        /// </summary>
        public async Task<(JobPostingCleanSignal? Signal, Pgvector.Vector? Embedding)> QuickExtractAndGroundAsync(string rawDescription)
        {
            try
            {
                var cleanSignal = await ExtractJobCleanSignalAsync(rawDescription);
                if (cleanSignal == null)
                {
                    _logger.LogWarning("QuickExtractAndGroundAsync: Clean Signal extraction returned null.");
                    return (null, null);
                }

                if (cleanSignal.RequiredSkills.Count < 3 && cleanSignal.Responsibilities.Count > 0)
                {
                    _logger.LogInformation("QuickExtractAndGroundAsync: Sparse extraction ({Count} skills). Running responsibilities fallback.", cleanSignal.RequiredSkills.Count);
                    var fallbackSkills = await ExtractSkillsFromResponsibilitiesAsync(cleanSignal.Responsibilities, cleanSignal.TargetRoles.FirstOrDefault()?.Title);
                    var existingNames = cleanSignal.RequiredSkills.Select(s => s.Name.ToLowerInvariant()).ToHashSet();
                    foreach (var skill in fallbackSkills)
                    {
                        if (!existingNames.Contains(skill.Name.ToLowerInvariant()))
                            cleanSignal.RequiredSkills.Add(skill);
                    }
                }

                // Ground against the reference dictionary (no DB write — grounding only mutates the in-memory signal)
                await GroundCleanSignalAsync(cleanSignal, "quick-match-ephemeral");

                // Generate embedding for cosine similarity
                var symmetricString = BuildSymmetricString(cleanSignal);
                var truncated = symmetricString.Length > 2000 ? symmetricString[..2000] : symmetricString;
                var embeddings = await _embeddingGenerator.GenerateAsync([truncated]);
                var vector = new Pgvector.Vector(embeddings[0].Vector);

                return (cleanSignal, vector);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QuickExtractAndGroundAsync failed.");
                return (null, null);
            }
        }

        public async Task<JobPostingCleanSignal?> ApplyGroundingCorrectionsAsync(Guid jobId, List<GroundingCorrectionItem> corrections)
        {
            var job = await _context.JobPostings.FindAsync(jobId);
            if (job?.CleanSignal == null) return null;

            var cs = job.CleanSignal;

            foreach (var correction in corrections)
            {
                var ungroundedMatch = cs.UngroundedSkills.FirstOrDefault(s =>
                    string.Equals(s.Name, correction.From, StringComparison.OrdinalIgnoreCase));
                if (ungroundedMatch != null)
                {
                    cs.UngroundedSkills.Remove(ungroundedMatch);
                    if (!cs.RequiredSkills.Any(s => string.Equals(s.Name, correction.To, StringComparison.OrdinalIgnoreCase)))
                    {
                        cs.RequiredSkills.Add(new JobSkill
                        {
                            Name = correction.To,
                            OriginalName = ungroundedMatch.Name,
                            Category = ungroundedMatch.Category,
                            Importance = ungroundedMatch.Importance,
                            YearsOfExperience = ungroundedMatch.YearsOfExperience
                        });
                    }
                    continue;
                }

                var groundedMatch = cs.RequiredSkills.FirstOrDefault(s =>
                    s.OriginalName != null &&
                    string.Equals(s.OriginalName, correction.From, StringComparison.OrdinalIgnoreCase));
                if (groundedMatch != null)
                {
                    if (string.IsNullOrEmpty(correction.To))
                    {
                        cs.RequiredSkills.Remove(groundedMatch);
                        if (!cs.UngroundedSkills.Any(s => string.Equals(s.Name, groundedMatch.OriginalName, StringComparison.OrdinalIgnoreCase)))
                        {
                            cs.UngroundedSkills.Add(new JobSkill
                            {
                                Name = groundedMatch.OriginalName!,
                                Category = groundedMatch.Category,
                                Importance = groundedMatch.Importance,
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
            job.Embedding = new Vector(emb[0].Vector);
            job.UpdatedAt = DateTime.UtcNow;

            _context.Entry(job).State = EntityState.Modified;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Applied {Count} grounding corrections to job {JobId}.", corrections.Count, jobId);
            return cs;
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