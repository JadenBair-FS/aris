using System.Text.Json.Serialization;

namespace ARIS.Shared.Models.Ingestion;

public class BridgePair
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";
}

public class DependencyPair
{
    [JsonPropertyName("child")]
    public string Child { get; set; } = "";

    [JsonPropertyName("parent")]
    public string Parent { get; set; } = "";
}