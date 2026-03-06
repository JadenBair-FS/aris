using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using ARIS.Shared.Helpers;
using ARIS.Shared.Models;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.Extensions.AI;
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
        private readonly OntologyExpansionService _expansionService;
        private readonly ILogger<JobService> _logger;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Converters = { new LenientStringConverter(), new LenientDoubleConverter() }
        };

        private const int MaxExtractionAttempts = 3;

        public JobService(ArisDbContext context, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IChatClient chatClient, OntologyExpansionService expansionService, ILogger<JobService> logger)
        {
            _context = context;
            _embeddingGenerator = embeddingGenerator;
            _chatClient = chatClient;
            _expansionService = expansionService;
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

                // Pre-generate the ID so we can pass it as the sourceDocId for ontology expansion.
                // Using recruiterId would cause all jobs from one recruiter to count as a single observation.
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
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["stream"] = false,
                        ["num_ctx"] = 2048,
                        ["num_gpu"] = 35,
                        ["num_thread"] = 8
                    }
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
                var unknownSkills = new List<(string Name, Vector Embedding)>();

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

                    if (domainMatch != null && domainMatch.Distance < 0.25)
                    {
                        signal.RequiredSkills[i].Name = domainMatch.Name;
                        continue;
                    }

                    var query = _context.Skills.Where(s => s.Embedding != null);
                    if (!isTech)
                        query = query.Where(s => s.Source != "Roadmap.sh");

                    var generalMatch = await query
                        .Select(s => new { s.Name, Distance = s.Embedding!.CosineDistance(vector) })
                        .OrderBy(x => x.Distance)
                        .FirstOrDefaultAsync();

                    if (generalMatch != null && generalMatch.Distance < 0.40)
                    {
                        signal.RequiredSkills[i].Name = generalMatch.Name;
                    }
                    else
                    {
                        // No acceptable canonical match — keep original LLM text, queue for ontology expansion
                        unknownSkills.Add((signal.RequiredSkills[i].Name, vector));
                        _logger.LogInformation("Skill '{Skill}' has no canonical match (best distance {Distance:F3}) — queued for ontology expansion.",
                            signal.RequiredSkills[i].Name, generalMatch?.Distance ?? 1.0);
                    }
                }

                signal.RequiredSkills = signal.RequiredSkills
                    .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new JobSkill
                    {
                        Name = g.First().Name,
                        Importance = g.Any(s => s.Importance == "Essential") ? "Essential" : g.First().Importance,
                        YearsOfExperience = g.Sum(s => s.YearsOfExperience)
                    })
                    .ToList();

                foreach (var (name, vector) in unknownSkills)
                    await _expansionService.RecordCandidateAsync(name, vector, domainPrefix, isTech, sourceDocId);
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
                var truncatedText = rawText.Length > 6000 ? rawText[..6000] : rawText;
                var embeddings = await _embeddingGenerator.GenerateAsync([truncatedText]);
                var vector = new Vector(embeddings[0].Vector);

                var topRoles = await _context.Roles
                    .Where(r => r.Embedding != null)
                    .Select(r => new { r.Title, r.OnetCode, Distance = r.Embedding!.CosineDistance(vector) })
                    .OrderBy(x => x.Distance)
                    .Take(5)
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
                        .Take(8)
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
                    .Take(15)
                    .ToListAsync())
                    .Where(s => !domainSet.Contains(s.Name))
                    .Select(s => s.Name)
                    .Take(7)
                    .ToList();

                var combinedSkills = domainSkillNames.Concat(globalSkillNames).ToList();

                return (
                    string.Join(", ", topRoles.Select(r => r.Title)),
                    string.Join(", ", combinedSkills)
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve reference vocabulary for job posting. Proceeding without it.");
                return ("", "");
            }
        }

        private async Task<JobPostingCleanSignal?> ExtractJobCleanSignalAsync(string rawText)
        {
            string userPrompt;
            try
            {
                var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "JobExtraction.md");
                var template = await File.ReadAllTextAsync(promptPath);

                var (refRoles, refSkills) = await RetrieveReferenceVocabularyAsync(rawText);

                userPrompt = template
                    .Replace("{reference_roles}", refRoles)
                    .Replace("{reference_skills}", refSkills)
                    .Replace("{raw_text}", rawText);

                _logger.LogInformation("Reference Roles Sent (Job): {Roles}", refRoles);
                _logger.LogInformation("Reference Skills Sent (Job): {Skills}", refSkills);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load Job Extraction prompt.");
                return null;
            }

            string? previousOutput = null;
            string? validationFailure = null;

            for (int attempt = 1; attempt <= MaxExtractionAttempts; attempt++)
            {
                try
                {
                    var messages = BuildJobExtractionMessages(userPrompt, attempt, validationFailure, previousOutput);
                    
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
                        _logger.LogWarning("LLM returned empty response for job (attempt {Attempt})", attempt);
                        continue;
                    }

                    _logger.LogInformation("LLM Response (Job attempt {Attempt}): {Json}", attempt, jsonString);

                    jsonString = UnwrapIfNeeded(jsonString);
                    jsonString = NormalizeJobFieldNames(jsonString);

                    var signal = JsonSerializer.Deserialize<JobPostingCleanSignal>(jsonString, _jsonOptions);
                    if (signal == null)
                    {
                        _logger.LogWarning("Deserialization returned null for job (attempt {Attempt})", attempt);
                        previousOutput = jsonString;
                        validationFailure = "Deserialization produced a null object.";
                        continue;
                    }

                    PostProcessJobCleanSignal(signal);

                    var validation = ValidateJobCleanSignal(signal);
                    if (validation == null)
                    {
                        _logger.LogInformation("Job Clean Signal extraction succeeded on attempt {Attempt}", attempt);
                        return signal;
                    }

                    _logger.LogWarning("Job Validation failed (attempt {Attempt}): {Reason}", attempt, validation);
                    previousOutput = jsonString;
                    validationFailure = validation;
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Job JSON parsing failed (attempt {Attempt})", attempt);
                    validationFailure = $"JSON parse error: {ex.Message}";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "LLM Job extraction failed (attempt {Attempt})", attempt);
                    return null;
                }
            }

            _logger.LogError("Job Clean Signal extraction failed after {MaxAttempts} attempts", MaxExtractionAttempts);
            return null;
        }

        private static List<ChatMessage> BuildJobExtractionMessages(string userPrompt, int attempt, string? validationFailure, string? previousOutput)
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You are a job-description-to-JSON extraction engine. You MUST output a single JSON object with exactly these 4 top-level keys: \"target_roles\", \"required_skills\", \"responsibilities\", \"minimum_education\". No other keys are allowed. For every skill: \"importance\" must be either \"Essential\" or \"Preferred\" — never null or any other value. \"years_of_experience\" must always be a number — NEVER null, use 0.0 if unknown."),
                new(ChatRole.User, userPrompt)
            };

            if (attempt > 1 && validationFailure != null && previousOutput != null)
            {
                messages.Add(new ChatMessage(ChatRole.User, BuildJobCorrectionPrompt(validationFailure, previousOutput)));
            }

            return messages;
        }

        private static string BuildJobCorrectionPrompt(string validationFailure, string previousOutput)
        {
            return $$"""
                Your previous output failed validation: {{validationFailure}}

                Previous output:
                {{previousOutput}}

                Please fix the issue and output the corrected JSON using EXACTLY this schema:

                {
                  "target_roles": [{ "title": "string", "priority": "Primary/Secondary" }],
                  "required_skills": [{ "name": "string", "importance": "Essential/Preferred", "years_of_experience": 0.0 }],
                  "responsibilities": ["string"],
                  "minimum_education": [{ "degree": "string", "required": true/false }]
                }

                Critical field requirements:
                - required_skills[].importance MUST be "Essential" or "Preferred" — never null, never any other value
                - required_skills[].years_of_experience MUST be a number — NEVER null, use 0.0 if unknown
                - required_skills[].name MUST be a concise skill name of 1-5 words — never a full sentence
                """;
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