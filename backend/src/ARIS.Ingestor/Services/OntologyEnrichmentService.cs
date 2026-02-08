using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARIS.Shared.Models.Ingestion;

namespace ARIS.Ingestor.Services;

public class OntologyEnrichmentService
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<OntologyEnrichmentService> _logger;
    private readonly string _promptPath;
    private readonly string _dependencyPromptPath;

    public OntologyEnrichmentService(IChatClient chatClient, ILogger<OntologyEnrichmentService> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
        _promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "OntologyEnrichment.md");
        _dependencyPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "DependencyDetection.md");
    }

    public async Task<List<BridgePair>> FindBridgesAsync(string roleName, List<string> skills, CancellationToken ct = default)
    {
        if (skills.Count < 2) return new List<BridgePair>();

        string promptTemplate;
        try
        {
             if (!File.Exists(_promptPath))
             {
                 var debugPath = Path.Combine(Directory.GetCurrentDirectory(), "../ARIS.Shared/Prompts/OntologyEnrichment.md");
                 if (File.Exists(debugPath))
                 {
                    promptTemplate = await File.ReadAllTextAsync(debugPath, ct);
                 }
                 else
                 {
                     _logger.LogError("Prompt file not found at {Path}", _promptPath);
                     return new List<BridgePair>();
                 }
             }
             else
             {
                 promptTemplate = await File.ReadAllTextAsync(_promptPath, ct);
             }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to read prompt file.");
             return new List<BridgePair>();
        }

        var prompt = promptTemplate
            .Replace("{role_name}", roleName)
            .Replace("{skills_json}", JsonSerializer.Serialize(skills));
        
        try
        {
            var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct);
            var text = response.Text?.Trim();

            if (string.IsNullOrEmpty(text)) return new List<BridgePair>();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // Strip markdown code fences if present (```json ... ``` or ``` ... ```)
            text = Regex.Replace(text, @"```(?:json)?\s*", "").Trim();
            text = Regex.Replace(text, @"//.*", "");

            try
            {
                var direct = JsonSerializer.Deserialize<List<BridgePair>>(text, options);
                if (direct != null) return direct.Where(x => !string.IsNullOrEmpty(x.Source) && !string.IsNullOrEmpty(x.Target)).ToList();
            }
            catch { }

            var match = Regex.Match(text, @"\[\s*\{.*\}\s*\]", RegexOptions.Singleline);
            if (match.Success)
            {
                try
                {
                    var result = JsonSerializer.Deserialize<List<BridgePair>>(match.Value, options);
                    return result?.Where(x => !string.IsNullOrEmpty(x.Source) && !string.IsNullOrEmpty(x.Target)).ToList() ?? new List<BridgePair>();
                }
                catch { }
            }

            if (Regex.IsMatch(text, @"\[\s*\]"))
                return new List<BridgePair>();

            _logger.LogWarning("Could not extract JSON array from LLM response. Response: {Response}", text);
            return new List<BridgePair>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze siblings for bridges.");
            return new List<BridgePair>();
        }
    }

    public async Task<List<DependencyPair>> FindDependenciesAsync(string roleName, List<string> skills, CancellationToken ct = default)
    {
        if (skills.Count < 2) return new List<DependencyPair>();

        string promptTemplate;
        try
        {
            if (!File.Exists(_dependencyPromptPath))
            {
                var debugPath = Path.Combine(Directory.GetCurrentDirectory(), "../ARIS.Shared/Prompts/DependencyDetection.md");
                if (File.Exists(debugPath))
                {
                    promptTemplate = await File.ReadAllTextAsync(debugPath, ct);
                }
                else
                {
                    _logger.LogError("Prompt file not found at {Path}", _dependencyPromptPath);
                    return new List<DependencyPair>();
                }
            }
            else
            {
                promptTemplate = await File.ReadAllTextAsync(_dependencyPromptPath, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read dependency prompt file.");
            return new List<DependencyPair>();
        }

        var prompt = promptTemplate
            .Replace("{role_name}", roleName)
            .Replace("{skills_json}", JsonSerializer.Serialize(skills));

        try
        {
            var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct);
            var text = response.Text?.Trim();

            if (string.IsNullOrEmpty(text)) return new List<DependencyPair>();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            text = Regex.Replace(text, @"```(?:json)?\s*", "").Trim();
            text = Regex.Replace(text, @"//.*", "");

            try
            {
                var direct = JsonSerializer.Deserialize<List<DependencyPair>>(text, options);
                if (direct != null) return direct.Where(x => !string.IsNullOrEmpty(x.Child) && !string.IsNullOrEmpty(x.Parent)).ToList();
            }
            catch { }

            var match = Regex.Match(text, @"\[\s*\{.*\}\s*\]", RegexOptions.Singleline);
            if (match.Success)
            {
                try
                {
                    var result = JsonSerializer.Deserialize<List<DependencyPair>>(match.Value, options);
                    return result?.Where(x => !string.IsNullOrEmpty(x.Child) && !string.IsNullOrEmpty(x.Parent)).ToList() ?? new List<DependencyPair>();
                }
                catch { }
            }

            if (Regex.IsMatch(text, @"\[\s*\]"))
                return new List<DependencyPair>();

            _logger.LogWarning("Could not extract JSON array from dependency LLM response. Response: {Response}", text);
            return new List<DependencyPair>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze role for dependencies.");
            return new List<DependencyPair>();
        }
    }
}
