using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using ARIS.Shared.Models.Ingestion.Roadmap;

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