using System.Text.Json.Serialization;

namespace ARIS.Shared.Models.CleanSignal;

public class JobPostingCleanSignal
{
    [JsonPropertyName("target_roles")]
    public List<JobRole> TargetRoles { get; set; } = [];

    [JsonPropertyName("required_skills")]
    public List<JobSkill> RequiredSkills { get; set; } = [];

    [JsonPropertyName("responsibilities")]
    public List<string> Responsibilities { get; set; } = [];

    [JsonPropertyName("minimum_education")]
    public List<JobEducation> MinimumEducation { get; set; } = [];
}

public class JobRole
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "Primary"; // Primary or Secondary
}

public class JobSkill
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("importance")]
    public string Importance { get; set; } = "Essential"; // Essential or Preferred
}

public class JobEducation
{
    [JsonPropertyName("degree")]
    public string Degree { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public string Required { get; set; } = string.Empty;
}