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

    [JsonPropertyName("ungrounded_skills")]
    public List<JobSkill> UngroundedSkills { get; set; } = [];
}

public class JobRole
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "Primary"; // Primary or Secondary

    [JsonIgnore]
    public string? OnetCode { get; set; }
}

public class JobSkill
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The raw name extracted by the LLM before grounding replaced it with a canonical name.
    /// Null when the extracted name already matched the canonical (no substitution occurred).
    /// </summary>
    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = "Technical"; // Technical or Soft

    [JsonPropertyName("importance")]
    public string Importance { get; set; } = "Essential"; // Essential or Preferred

    [JsonPropertyName("years_of_experience")]
    public double YearsOfExperience { get; set; }
}

public class JobEducation
{
    [JsonPropertyName("degree")]
    public string Degree { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public string Required { get; set; } = string.Empty;
}