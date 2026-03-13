using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ARIS.Shared.Models.Ingestion.Roadmap;

namespace ARIS.Ingestor.Services;

public class RoadmapService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RoadmapService> _logger;
    private readonly string _cacheDirectory;

    public RoadmapService(HttpClient httpClient, ILogger<RoadmapService> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.BaseAddress = new Uri("https://roadmap.sh/");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        _cacheDirectory = configuration["Roadmap:CacheDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "Roadmaps");

        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<RoadmapDto?> GetRoadmapAsync(string slug, CancellationToken cancellationToken = default)
    {
        var cacheFile = Path.Combine(_cacheDirectory, $"{slug}.json");

        // Return cached file if it exists
        if (File.Exists(cacheFile))
        {
            _logger.LogInformation("Loading roadmap from cache: {Slug}", slug);
            try
            {
                await using var stream = File.OpenRead(cacheFile);
                return await JsonSerializer.DeserializeAsync<RoadmapDto>(stream, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read cached roadmap for '{Slug}' — fetching fresh copy.", slug);
            }
        }

        // Fetch from API and save to cache
        try
        {
            _logger.LogInformation("Fetching roadmap from API: {Slug}", slug);
            var roadmap = await _httpClient.GetFromJsonAsync<RoadmapDto>($"{slug}.json", cancellationToken);

            if (roadmap != null)
            {
                try
                {
                    var json = JsonSerializer.Serialize(roadmap, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(cacheFile, json, cancellationToken);
                    _logger.LogInformation("Cached roadmap saved: {File}", cacheFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cache roadmap '{Slug}' — continuing without cache.", slug);
                }
            }

            return roadmap;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch roadmap: {Slug}", slug);
            return null;
        }
    }
}
