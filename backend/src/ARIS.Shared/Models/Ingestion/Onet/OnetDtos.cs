using System.Text.Json.Serialization;

namespace ARIS.Shared.Models.Ingestion.Onet;

public class OccupationListResponse
{
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("next")]
    public string? Next { get; set; }

    [JsonPropertyName("occupation")]
    public List<OccupationListDto>? Occupation { get; set; }
}

public class OccupationListDto
{
    [JsonPropertyName("code")]
    public required string Code { get; set; }

    [JsonPropertyName("title")]
    public required string Title { get; set; }
}

public class OccupationSummaryDto
{
    [JsonPropertyName("code")]
    public required string Code { get; set; }

    [JsonPropertyName("title")]
    public required string Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public class TasksResponse
{
    [JsonPropertyName("task")]
    public List<TaskDto>? Task { get; set; }
}

public class TaskDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public required string Title { get; set; }
}

public class SkillsResponse
{
    [JsonPropertyName("element")]
    public List<SkillElementDto>? Element { get; set; }
}

public class SkillElementDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public class OccupationDetailsDto
{
    public required string Code { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public List<string> Tasks { get; set; } = new();
    public List<string> Skills { get; set; } = new();         // cognitive/psychomotor (35 O*NET skills)
    public List<string> Knowledge { get; set; } = new();      // domain knowledge areas
    public List<string> WorkActivities { get; set; } = new(); // on-the-job activities
    public List<string> TechnologySkills { get; set; } = new(); // specific tools/software
}

public class TechnologySkillsResponse
{
    [JsonPropertyName("category")]
    public List<TechnologySkillCategory>? Category { get; set; }
}

public class TechnologySkillCategory
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("example")]
    public List<TechnologySkillExample>? Example { get; set; }

    // Additional examples beyond the primary list
    [JsonPropertyName("example_more")]
    public List<TechnologySkillExample>? ExampleMore { get; set; }
}

public class TechnologySkillExample
{
    // API returns "title", not "name"
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("hot_technology")]
    public bool HotTechnology { get; set; }

    [JsonPropertyName("in_demand")]
    public bool InDemand { get; set; }
}