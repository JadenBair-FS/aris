using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARIS.API.Services;

public class PersonalInfo
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Location { get; set; } = "";
    public string LinkedIn { get; set; } = "";
    public string GitHub { get; set; } = "";
    public string Website { get; set; } = "";
}

public class PersonalInfoExtractor
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<PersonalInfoExtractor> _logger;

    public PersonalInfoExtractor(IChatClient chatClient, ILogger<PersonalInfoExtractor> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    public async Task<PersonalInfo> ExtractAsync(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new PersonalInfo();

        var snippet = rawText.Length > 2000 ? rawText[..2000] : rawText;

        var prompt = $$"""
            Extract contact information from the resume text below.
            Return JSON only, no other text: {"name":"","email":"","phone":"","location":"","linkedin":"","github":"","website":""}
            Use empty string for any field not found. Do not guess or invent information.

            RESUME TEXT:
            {{snippet}}
            """;

        try
        {
            var response = await _chatClient.GetResponseAsync(prompt);
            var text = response?.Text?.Trim() ?? "";

            var json = Regex.Replace(text, @"```(?:json)?", "").Trim();

            var result = JsonSerializer.Deserialize<PersonalInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return result ?? new PersonalInfo();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract personal info from resume text.");
            return new PersonalInfo();
        }
    }
}
