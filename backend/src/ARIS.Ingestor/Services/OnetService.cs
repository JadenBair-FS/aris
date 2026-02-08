using System.Net.Http.Json;
using System.Text.Json.Serialization;
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
            try 
            {
                var tasksResponse = await _httpClient.GetFromJsonAsync<TasksResponse>($"online/occupations/{onetCode}/summary/tasks?start=1&end=20", cancellationToken);
                if (tasksResponse?.Task != null)
                {
                    details.Tasks = tasksResponse.Task.Select(t => t.Title).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fetch tasks for {Code}", onetCode);
            }

            // Get Skills
            // Endpoint: /online/occupations/{code}/summary/skills
            try
            {
                var skillsResponse = await _httpClient.GetFromJsonAsync<SkillsResponse>($"online/occupations/{onetCode}/summary/skills?start=1&end=20", cancellationToken);
                if (skillsResponse?.Element != null)
                {
                    details.Skills = skillsResponse.Element.Select(s => s.Name).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fetch skills for {Code}", onetCode);
            }
            
            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching details for {Code}", onetCode);
            return null;
        }
    }
}