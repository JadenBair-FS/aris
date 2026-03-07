using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARIS.Shared.Models.Ingestion;

namespace ARIS.Ingestor.Services;

public class OntologyEnrichmentService
{
    private readonly IChatClient _chatClient;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ILogger<OntologyEnrichmentService> _logger;
    private readonly string _promptPath;
    private readonly string _dependencyPromptPath;

    public OntologyEnrichmentService(
        IChatClient chatClient, 
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILogger<OntologyEnrichmentService> logger)
    {
        _chatClient = chatClient;
        _embeddingGenerator = embeddingGenerator;
        _logger = logger;
        _promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "OntologyEnrichment.md");
        _dependencyPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "DependencyDetection.md");
    }

    public async Task<List<BridgePair>> FindBridgesAsync(string roleName, List<string> skills, List<(string Child, string Parent)> hierarchies, CancellationToken ct = default, string? promptOverridePath = null, int timeoutSeconds = 120)
    {
        if (skills.Count < 2) return new List<BridgePair>();

        string promptTemplate;
        try
        {
            var effectivePath = promptOverridePath ?? _promptPath;
            var fallbackPath = Path.Combine(Directory.GetCurrentDirectory(), "../ARIS.Shared/Prompts", Path.GetFileName(effectivePath));
            var path = File.Exists(effectivePath) ? effectivePath : fallbackPath;
            promptTemplate = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to read prompt file.");
             return new List<BridgePair>();
        }

        var skillList = string.Join("\n", skills.Select(s => $"- {s}"));
        var prompt = promptTemplate
            .Replace("{role_name}", roleName)
            .Replace("{skills_json}", skillList);

        var text = string.Empty;
        try
        {
            _logger.LogInformation("Analyzing {Count} skills for bridges in role '{Role}' (Single Pass)", skills.Count, roleName);

            var messages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, "You are an exhaustive Data Extraction tool. Your goal is to identify ALL possible highly transferable skill pairs from the provided list. Return a JSON object with a 'bridges' key. Be comprehensive and thorough."),
                new ChatMessage(ChatRole.User, prompt)
            };

            var chatOptions = new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.Json,
                Temperature = 0.1f,
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var response = await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken: timeoutCts.Token);
            text = response.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(text)) return new List<BridgePair>();

            // Clean up markdown code blocks if present
            if (text.Contains("```json"))
            {
                text = text.Split("```json")[1].Split("```")[0].Trim();
            }
            else if (text.Contains("```"))
            {
                text = text.Split("```")[1].Split("```")[0].Trim();
            }

            using var doc = JsonDocument.Parse(text);
            var result = new List<BridgePair>();

            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("bridges", out var bridgesProp))
            {
                if (bridgesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in bridgesProp.EnumerateArray())
                    {
                        var pair = ParseBridgePair(item);
                        if (pair != null) result.Add(pair);
                    }
                    _logger.LogInformation("  + Found {Count} bridges (from 'bridges' key).", result.Count);
                    return result;
                }
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var pair = ParseBridgePair(item);
                    if (pair != null) result.Add(pair);
                }
                _logger.LogInformation("  + Found {Count} bridges.", result.Count);
                return result;
            }
            
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Check if it's a single bridge pair
                var singlePair = ParseBridgePair(doc.RootElement);
                if (singlePair != null)
                {
                    _logger.LogInformation("  + Found 1 bridge (single object).");
                    return new List<BridgePair> { singlePair };
                }

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            var pair = ParseBridgePair(item);
                            if (pair != null) result.Add(pair);
                        }
                        _logger.LogInformation("  + Found {Count} bridges (nested).", result.Count);
                        return result;
                    }
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("LLM timeout after {Seconds}s for bridge detection in role '{Role}' — skipping.", timeoutSeconds, roleName);
            return new List<BridgePair>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse JSON from: {Text}. Error: {Msg}", text, ex.Message);
            return new List<BridgePair>();
        }
    }

    private BridgePair? ParseBridgePair(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        string? source = null;
        string? target = null;

        if (element.TryGetProperty("source", out var sProp))
        {
            source = sProp.ValueKind switch
            {
                JsonValueKind.String => sProp.GetString(),
                JsonValueKind.Array when sProp.GetArrayLength() > 0 => sProp[0].GetString(),
                _ => null
            };
        }

        if (element.TryGetProperty("target", out var tProp))
        {
            target = tProp.ValueKind switch
            {
                JsonValueKind.String => tProp.GetString(),
                JsonValueKind.Array when tProp.GetArrayLength() > 0 => tProp[0].GetString(),
                _ => null
            };
        }

        if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(target))
        {
            return new BridgePair { Source = source, Target = target };
        }

        return null;
    }

    public async Task<List<DependencyPair>> FindDependenciesAsync(string roleName, List<string> skills, CancellationToken ct = default, int timeoutSeconds = 120)
    {
        if (skills.Count < 2) return new List<DependencyPair>();

        string promptTemplate;
        try
        {
            var path = File.Exists(_dependencyPromptPath) ? _dependencyPromptPath : Path.Combine(Directory.GetCurrentDirectory(), "../ARIS.Shared/Prompts/DependencyDetection.md");
            promptTemplate = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read dependency prompt file.");
            return new List<DependencyPair>();
        }

        var skillList = string.Join("\n", skills.Select(s => $"- {s}"));
        var prompt = promptTemplate
            .Replace("{role_name}", roleName)
            .Replace("{skills_json}", skillList);

        var text = string.Empty;
        try
        {
            _logger.LogInformation("Analyzing {Count} skills for dependencies in role '{Role}' (Single Pass)", skills.Count, roleName);

            var messages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, "You are an exhaustive Data Extraction tool. Your goal is to identify ALL technical dependencies (prerequisites) from the provided list. Return a JSON object with a 'dependencies' key. Be comprehensive and thorough."),
                new ChatMessage(ChatRole.User, prompt)
            };

            var chatOptions = new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.Json,
                Temperature = 0.1f,
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var response = await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken: timeoutCts.Token);
            text = response.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(text)) return new List<DependencyPair>();

            // Clean up markdown code blocks if present
            if (text.Contains("```json"))
            {
                text = text.Split("```json")[1].Split("```")[0].Trim();
            }
            else if (text.Contains("```"))
            {
                text = text.Split("```")[1].Split("```")[0].Trim();
            }

            using var doc = JsonDocument.Parse(text);
            var result = new List<DependencyPair>();

   
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("dependencies", out var depsProp))
            {
                if (depsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in depsProp.EnumerateArray())
                    {
                        var pair = ParseDependencyPair(item);
                        if (pair != null) result.Add(pair);
                    }
                    _logger.LogInformation("  + Found {Count} dependencies (from 'dependencies' key).", result.Count);
                    return result;
                }
            }
            
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var pair = ParseDependencyPair(item);
                    if (pair != null) result.Add(pair);
                }
                _logger.LogInformation("  + Found {Count} dependencies.", result.Count);
                return result;
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Check if it's a single dependency pair
                var singlePair = ParseDependencyPair(doc.RootElement);
                if (singlePair != null)
                {
                    _logger.LogInformation("  + Found 1 dependency (single object).");
                    return new List<DependencyPair> { singlePair };
                }

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            var pair = ParseDependencyPair(item);
                            if (pair != null) result.Add(pair);
                        }
                        _logger.LogInformation("  + Found {Count} dependencies (nested).", result.Count);
                        return result;
                    }
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("LLM timeout after {Seconds}s for dependency detection in role '{Role}' — skipping.", timeoutSeconds, roleName);
            return new List<DependencyPair>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse dependency JSON from: {Text}. Error: {Msg}", text, ex.Message);
            return new List<DependencyPair>();
        }
    }

    private DependencyPair? ParseDependencyPair(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        string? child = null;
        string? parent = null;

        if (element.TryGetProperty("child", out var cProp))
        {
            child = cProp.ValueKind switch
            {
                JsonValueKind.String => cProp.GetString(),
                JsonValueKind.Array when cProp.GetArrayLength() > 0 => cProp[0].GetString(),
                _ => null
            };
        }

        if (element.TryGetProperty("parent", out var pProp))
        {
            parent = pProp.ValueKind switch
            {
                JsonValueKind.String => pProp.GetString(),
                JsonValueKind.Array when pProp.GetArrayLength() > 0 => pProp[0].GetString(),
                _ => null
            };
        }

        if (!string.IsNullOrWhiteSpace(child) && !string.IsNullOrWhiteSpace(parent))
        {
            return new DependencyPair { Child = child, Parent = parent };
        }

        return null;
    }
}
