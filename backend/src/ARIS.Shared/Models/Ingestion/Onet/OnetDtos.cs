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
    public List<string> TechnologySkills { get; set; } = new();
}

public class SkillTaxonomyNode
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("child")]
    public List<SkillTaxonomyNode>? Child { get; set; }
}

public class TechnologySkillsResponse
{
    [JsonPropertyName("category")]
    public List<TechnologySkillCategory>? Category { get; set; }
}

public class TechnologySkillCategory
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("example")]
    public List<TechnologySkillExample>? Example { get; set; }

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

// Generic paginated element response — used by skills, abilities, knowledge, work_activities
public class OnetElementResponse
{
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("next")]
    public string? Next { get; set; }

    [JsonPropertyName("element")]
    public List<OnetElement> Element { get; set; } = new();
}

public class OnetElement
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("importance")]
    public int? Importance { get; set; }
}

// Tasks use a "task" array with "statement" field instead of "name"
public class OnetTaskResponse
{
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("next")]
    public string? Next { get; set; }

    [JsonPropertyName("task")]
    public List<OnetDetailTask> Tasks { get; set; } = new();
}

public class OnetDetailTask
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("statement")]
    public string Statement { get; set; } = string.Empty;

    [JsonPropertyName("importance")]
    public int? Importance { get; set; }
}

// Job zone
public class OnetJobZoneResponse
{
    [JsonPropertyName("job_zone")]
    public OnetJobZoneDetail? JobZone { get; set; }
}

public class OnetJobZoneDetail
{
    [JsonPropertyName("value")]
    public int Value { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}