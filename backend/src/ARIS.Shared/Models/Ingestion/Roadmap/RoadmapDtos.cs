using System.Text.Json.Serialization;

namespace ARIS.Shared.Models.Ingestion.Roadmap;

public class RoadmapDto
{
    [JsonPropertyName("title")]
    public RoadmapTitleDto? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("nodes")]
    public List<RoadmapNodeDto>? Nodes { get; set; }

    [JsonPropertyName("edges")]
    public List<RoadmapEdgeDto>? Edges { get; set; }
}

public class RoadmapEdgeDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }
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

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("data")]
    public RoadmapNodeDataDto? Data { get; set; }
}

public class RoadmapNodeDataDto
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }
}