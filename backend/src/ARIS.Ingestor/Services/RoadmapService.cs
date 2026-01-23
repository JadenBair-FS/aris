using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ARIS.Ingestor.Services;

public class RoadmapService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RoadmapService> _logger;

    public RoadmapService(HttpClient httpClient, ILogger<RoadmapService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.BaseAddress = new Uri("https://roadmap.sh/");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public async Task<RoadmapDto?> GetRoadmapAsync(string slug, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Fetching roadmap for: {Slug}", slug);
            return await _httpClient.GetFromJsonAsync<RoadmapDto>($"{slug}.json", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch roadmap: {Slug}", slug);
            return null;
        }
    }
}

// --- DTOs for Roadmap.sh JSON Structure ---

public class RoadmapDto
{
    [JsonPropertyName("title")]
    public RoadmapTitleDto? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("nodes")]
    public List<RoadmapNodeDto>? Nodes { get; set; }
}

public class RoadmapTitleDto
{
    [JsonPropertyName("card")]
    public string? Card { get; set; }

    [JsonPropertyName("page")]
    public string? Page { get; set; }
}

public class RoadmapNodeDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("data")]
    public RoadmapNodeDataDto? Data { get; set; }
}

public class RoadmapNodeDataDto
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }
}