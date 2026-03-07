using ARIS.Shared.Data;
using ARIS.Shared.Helpers;
using ARIS.Shared.Models;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.Extensions.AI;
using OllamaSharp;
using ElBruno.OllamaSharp.Extensions;
using Pgvector;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ARIS.API.Services;

public class ExtractionBenchmarkService
{
    private readonly ArisDbContext _context;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ILogger<ExtractionBenchmarkService> _logger;

    private static readonly Uri OllamaUri = new("http://192.168.4.45:11434");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new LenientStringConverter(), new LenientDoubleConverter() }
    };

    public ExtractionBenchmarkService(
        ArisDbContext context,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILogger<ExtractionBenchmarkService> logger)
    {
        _context = context;
        _embeddingGenerator = embeddingGenerator;
        _logger = logger;
    }

    public async Task<ExtractionBenchmarkResponse> RunBenchmarkAsync(ExtractionBenchmarkRequest request)
    {
        var response = new ExtractionBenchmarkResponse
        {
            Type = request.Type,
            Runs = request.Runs
        };

        // Retrieve reference vocabulary once — not part of the timed section
        var (refRoles, refSkills) = await RetrieveReferenceVocabularyAsync(request.Text);
        var softSkills = await RetrieveSoftSkillsAsync();

        string userPrompt;
        try
        {
            var promptFile = request.Type == "resume" ? "ResumeExtraction.md" : "JobExtraction.md";
            var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", promptFile);
            var template = await File.ReadAllTextAsync(promptPath);
            userPrompt = template
                .Replace("{reference_roles}", refRoles)
                .Replace("{reference_skills}", refSkills)
                .Replace("{soft_skills}", softSkills)
                .Replace("{raw_text}", request.Text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load prompt template for benchmark.");
            return response;
        }

        var systemMessage = request.Type == "resume"
            ? "You are a resume-to-JSON extraction engine. You MUST output a single JSON object with exactly these 4 top-level keys: \"roles\", \"skills\", \"experience_summary\", \"education\". No other keys are allowed. Do not nest the result inside a wrapper object. For every skill: \"years_of_experience\" must always be a number — NEVER null, use 0.0 if unknown. All \"year\" values must be strings (e.g. \"2019\", not 2019)."
            : "You are a job-description-to-JSON extraction engine. You MUST output a single JSON object with exactly these 4 top-level keys: \"target_roles\", \"required_skills\", \"responsibilities\", \"minimum_education\". No other keys are allowed. For every skill: \"importance\" must be either \"Essential\" or \"Preferred\" — never null or any other value. \"years_of_experience\" must always be a number — NEVER null, use 0.0 if unknown.";

        var chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json,
            Temperature = 0.1f,
        };

        foreach (var modelName in request.Models)
        {
            var result = new ModelBenchmarkResult();
            try
            {
                var apiClient = new OllamaApiClient(OllamaUri, modelName);
                apiClient.SetTimeout(TimeSpan.FromHours(1));
                IChatClient client = apiClient;

                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, systemMessage),
                    new(ChatRole.User, userPrompt)
                };

                string? lastJson = null;

                for (int run = 1; run <= request.Runs; run++)
                {
                    var sw = Stopwatch.StartNew();
                    var llmResponse = await client.GetResponseAsync(messages, chatOptions);
                    sw.Stop();

                    result.LatencyMs.Add(sw.ElapsedMilliseconds);
                    lastJson = llmResponse?.Text?.Trim();
                    _logger.LogInformation("Benchmark [{Model}] run {Run}/{Total}: {Ms}ms", modelName, run, request.Runs, sw.ElapsedMilliseconds);
                }

                result.AvgLatencyMs = result.LatencyMs.Count > 0
                    ? result.LatencyMs.Average()
                    : 0;

                // Parse last run's output for quality comparison
                if (!string.IsNullOrWhiteSpace(lastJson))
                {
                    if (request.Type == "resume")
                    {
                        lastJson = UnwrapIfNeeded(lastJson);
                        var signal = JsonSerializer.Deserialize<ResumeCleanSignal>(lastJson, _jsonOptions);
                        if (signal != null)
                        {
                            PostProcessResumeSignal(signal);
                            result.Output = signal;
                            result.SkillCount = signal.Skills.Count;
                            result.RoleCount = signal.Roles.Count;
                        }
                    }
                    else
                    {
                        lastJson = UnwrapIfNeeded(lastJson);
                        lastJson = NormalizeJobFieldNames(lastJson);
                        var signal = JsonSerializer.Deserialize<JobPostingCleanSignal>(lastJson, _jsonOptions);
                        if (signal != null)
                        {
                            PostProcessJobSignal(signal);
                            result.Output = signal;
                            result.SkillCount = signal.RequiredSkills.Count;
                            result.RoleCount = signal.TargetRoles.Count;
                        }
                    }
                }

                result.Success = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Benchmark run failed for model {Model}", modelName);
                result.Success = false;
                result.Error = ex.Message;
            }

            response.Results[modelName] = result;
        }

        response.Comparison = BuildComparison(request.Models, response.Results);
        return response;
    }

    private static BenchmarkComparison BuildComparison(List<string> models, Dictionary<string, ModelBenchmarkResult> results)
    {
        var comparison = new BenchmarkComparison();

        foreach (var (model, result) in results)
            comparison.SkillCounts[model] = result.SkillCount;

        var successfulResults = results
            .Where(r => r.Value.Success && r.Value.AvgLatencyMs > 0)
            .ToList();

        if (successfulResults.Count >= 2)
        {
            var fastest = successfulResults.MinBy(r => r.Value.AvgLatencyMs);
            var slowest = successfulResults.MaxBy(r => r.Value.AvgLatencyMs);
            comparison.FasterModel = fastest!.Key;
            comparison.SpeedupFactor = Math.Round(slowest!.Value.AvgLatencyMs / fastest.Value.AvgLatencyMs, 2);
        }
        else if (successfulResults.Count == 1)
        {
            comparison.FasterModel = successfulResults[0].Key;
            comparison.SpeedupFactor = 1.0;
        }

        var mistralKey = models.FirstOrDefault(m => m.StartsWith("mistral", StringComparison.OrdinalIgnoreCase));
        var otherKey = models.FirstOrDefault(m => !m.StartsWith("mistral", StringComparison.OrdinalIgnoreCase));

        if (mistralKey != null && otherKey != null &&
            results.TryGetValue(mistralKey, out var mistralResult) &&
            results.TryGetValue(otherKey, out var otherResult) &&
            mistralResult.Success && otherResult.Success)
        {
            var mistralSkills = ExtractSkillNames(mistralResult.Output);
            var otherSkills = ExtractSkillNames(otherResult.Output);

            comparison.SharedSkills = mistralSkills
                .Intersect(otherSkills, StringComparer.OrdinalIgnoreCase)
                .ToList();

            comparison.UniqueToLargerModel = mistralSkills
                .Except(otherSkills, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return comparison;
    }

    private static List<string> ExtractSkillNames(object? output)
    {
        return output switch
        {
            JobPostingCleanSignal job => job.RequiredSkills.Select(s => s.Name).ToList(),
            ResumeCleanSignal resume => resume.Skills.Select(s => s.Name).ToList(),
            _ => []
        };
    }

    // Duplicated from JobService — kept here to isolate benchmark scope
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
            _logger.LogWarning(ex, "Failed to retrieve reference vocabulary for benchmark. Proceeding without it.");
            return ("", "");
        }
    }

    private async Task<string> RetrieveSoftSkillsAsync()
    {
        try
        {
            var names = await _context.Skills
                .Where(s => s.Source == "ONET_Taxonomy")
                .OrderBy(s => s.Name)
                .Select(s => s.Name)
                .ToListAsync();
            return string.Join(", ", names);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve soft skills vocabulary for benchmark. Proceeding without it.");
            return "";
        }
    }

    private static void PostProcessJobSignal(JobPostingCleanSignal signal)
    {
        signal.TargetRoles.RemoveAll(r => string.IsNullOrWhiteSpace(r.Title));
        foreach (var role in signal.TargetRoles) role.Title = (role.Title ?? "").Trim();
        signal.RequiredSkills.RemoveAll(s => string.IsNullOrWhiteSpace(s.Name));
        foreach (var skill in signal.RequiredSkills) skill.Name = (skill.Name ?? "").Trim();
        signal.RequiredSkills.RemoveAll(s => s.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 7);
    }

    private static void PostProcessResumeSignal(ResumeCleanSignal signal)
    {
        signal.Roles.RemoveAll(r => string.IsNullOrWhiteSpace(r.Title));
        foreach (var role in signal.Roles) role.Title = (role.Title ?? "").Trim();
        signal.Skills.RemoveAll(s => string.IsNullOrWhiteSpace(s.Name));
        foreach (var skill in signal.Skills) skill.Name = (skill.Name ?? "").Trim();
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
                                WriteNormalizedJobObject(writer, item, targetName);
                            else
                                item.WriteTo(writer);
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
            if (root.ValueKind != JsonValueKind.Object) return jsonString;

            var properties = root.EnumerateObject().ToList();
            if (properties.Count == 1 && properties[0].Value.ValueKind == JsonValueKind.Object)
            {
                var inner = properties[0].Value;
                if (inner.TryGetProperty("target_roles", out _) || inner.TryGetProperty("required_skills", out _) ||
                    inner.TryGetProperty("roles", out _) || inner.TryGetProperty("skills", out _))
                {
                    return inner.GetRawText();
                }
            }
        }
        catch { }
        return jsonString;
    }
}
