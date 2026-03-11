using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using ARIS.Shared.Models.Ingestion.Onet;

namespace ARIS.Ingestor.Services;

public class OnetService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OnetService> _logger;

    public OnetService(HttpClient httpClient, IConfiguration config, ILogger<OnetService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var username = config["Onet:API_KEY"];
        var apiKey = config["Onet:API_KEY"]; 

        if (!string.IsNullOrEmpty(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
        else
        {
            _logger.LogWarning("O*NET API Key not found in configuration.");
        }
        
        _httpClient.BaseAddress = new Uri("https://api-v2.onetcenter.org/");
    }

    public async Task<List<OccupationListDto>> GetAllOccupationsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching all occupations from O*NET...");
        var occupations = new List<OccupationListDto>();
        int start = 1;
        int end = 50; 
        string? nextUrl = $"online/career_clusters/all?start={start}&end={end}";

        try 
        {
            while (!string.IsNullOrEmpty(nextUrl) && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Fetching batch: {Url}", nextUrl);
                var response = await _httpClient.GetFromJsonAsync<OccupationListResponse>(nextUrl, cancellationToken);
                
                if (response?.Occupation != null)
                {
                    occupations.AddRange(response.Occupation);
                }

                if (!string.IsNullOrEmpty(response?.Next))
                {
                   
                    var nextUri = new Uri(response.Next);
                    nextUrl = nextUri.PathAndQuery;
                    
                    // Safety break removed for full ingestion
                    // if (occupations.Count >= 50) break; 
                }
                else
                {
                    nextUrl = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching occupations.");
        }

        // Deduplicate based on Code
        return occupations.GroupBy(o => o.Code).Select(g => g.First()).ToList();
    }

    public async Task<OccupationDetailsDto?> GetOccupationDetailsAsync(string onetCode, CancellationToken cancellationToken = default)
    {
        try 
        {
            // Get Basic Details (Title, Description)
            // Endpoint: /online/occupations/{code}/
            var summary = await _httpClient.GetFromJsonAsync<OccupationSummaryDto>($"online/occupations/{onetCode}/", cancellationToken);
            
            if (summary == null) return null;

            var details = new OccupationDetailsDto
            {
                Code = summary.Code,
                Title = summary.Title,
                Description = summary.Description
            };

            // Get Tasks
            // Endpoint: /online/occupations/{code}/summary/tasks
            //try 
            //{
            //    var tasksResponse = await _httpClient.GetFromJsonAsync<TasksResponse>($"online/occupations/{onetCode}/summary/tasks?start=1&end=20", cancellationToken);
            //    if (tasksResponse?.Task != null)
            //    {
            //        details.Tasks = tasksResponse.Task.Select(t => t.Title).ToList();
            //    }
            //}
            //catch (Exception ex)
            //{
            //    _logger.LogWarning(ex, "Could not fetch tasks for {Code}", onetCode);
            //}

            // Get Technology Skills (specific software/tools used on the job)
            // Endpoint: /online/occupations/{code}/summary/technology_skills
            // Note: API returns "title" field on examples, not "name". Also includes "example_more".
            try
            {
                var techResponse = await _httpClient.GetFromJsonAsync<TechnologySkillsResponse>($"online/occupations/{onetCode}/summary/technology_skills", cancellationToken);
                if (techResponse?.Category != null)
                {
                    details.TechSkillCategories = techResponse.Category;
                    details.TechnologySkills = techResponse.Category
                        .SelectMany(c => (c.Example ?? []).Concat(c.ExampleMore ?? []))
                        .Where(e => !string.IsNullOrWhiteSpace(e.Title))
                        .Select(e => e.Title!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fetch technology skills for {Code}", onetCode);
            }

            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching details for {Code}", onetCode);
            return null;
        }
    }

    public async Task<List<SkillTaxonomyNode>> GetSkillTaxonomyAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<SkillTaxonomyNode>>(endpoint, cancellationToken);
            return result ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching skill taxonomy from {Endpoint}", endpoint);
            return [];
        }
    }

    public async Task<List<OnetElement>> GetOccupationSkillsAsync(string onetCode, CancellationToken cancellationToken = default)
        => await GetOccupationElementsAsync(onetCode, "skills", cancellationToken);

    public async Task<List<OnetElement>> GetOccupationKnowledgeAsync(string onetCode, CancellationToken cancellationToken = default)
        => await GetOccupationElementsAsync(onetCode, "knowledge", cancellationToken);

    public async Task<List<OnetElement>> GetOccupationAbilitiesAsync(string onetCode, CancellationToken cancellationToken = default)
        => await GetOccupationElementsAsync(onetCode, "abilities", cancellationToken);

    public async Task<List<OnetElement>> GetOccupationWorkActivitiesAsync(string onetCode, CancellationToken cancellationToken = default)
        => await GetOccupationElementsAsync(onetCode, "work_activities", cancellationToken);

    public async Task<List<OnetDetailTask>> GetOccupationTasksAsync(string onetCode, CancellationToken cancellationToken = default)
    {
        const int pageSize = 100;
        const int cap = 200;
        var results = new List<OnetDetailTask>();
        string? nextUrl = $"online/occupations/{onetCode}/details/tasks?start=1&end={pageSize}";

        try
        {
            while (!string.IsNullOrEmpty(nextUrl) && results.Count < cap && !cancellationToken.IsCancellationRequested)
            {
                var response = await _httpClient.GetFromJsonAsync<OnetTaskResponse>(nextUrl, cancellationToken);
                if (response?.Tasks != null)
                    results.AddRange(response.Tasks);

                if (!string.IsNullOrEmpty(response?.Next) && results.Count < cap)
                {
                    var nextUri = new Uri(response.Next);
                    nextUrl = nextUri.PathAndQuery;
                }
                else
                {
                    nextUrl = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch tasks for {Code}", onetCode);
        }

        return results.Count > cap ? results.Take(cap).ToList() : results;
    }

    public async Task<int?> GetOccupationJobZoneAsync(string onetCode, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<OnetJobZoneResponse>(
                $"online/occupations/{onetCode}/details/job_zone", cancellationToken);
            return response?.JobZone?.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch job zone for {Code}", onetCode);
            return null;
        }
    }

    // Shared pagination helper for element-based endpoints (skills, knowledge, abilities, work_activities)
    private async Task<List<OnetElement>> GetOccupationElementsAsync(
        string onetCode, string detailType, CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        const int cap = 200;
        var results = new List<OnetElement>();
        string? nextUrl = $"online/occupations/{onetCode}/details/{detailType}?start=1&end={pageSize}";

        try
        {
            while (!string.IsNullOrEmpty(nextUrl) && results.Count < cap && !cancellationToken.IsCancellationRequested)
            {
                var response = await _httpClient.GetFromJsonAsync<OnetElementResponse>(nextUrl, cancellationToken);
                if (response?.Element != null)
                    results.AddRange(response.Element);

                if (!string.IsNullOrEmpty(response?.Next) && results.Count < cap)
                {
                    var nextUri = new Uri(response.Next);
                    nextUrl = nextUri.PathAndQuery;
                }
                else
                {
                    nextUrl = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch {DetailType} for {Code}", detailType, onetCode);
        }

        return results.Count > cap ? results.Take(cap).ToList() : results;
    }
}