namespace ARIS.Shared.Models;

public class MatchAnalysisResult
{
    public Guid JobId { get; set; }
    public double VectorSimilarity { get; set; }
    public List<string> MatchingSkills { get; set; } = new();
    public List<string> ImplicitlyDiscoveredSkills { get; set; } = new();
    public List<string> MissingSkills { get; set; } = new();
    public List<string> BridgeableSkills { get; set; } = new();
    public List<string> PrerequisiteMetSkills { get; set; } = new();
    public List<string> HardGaps { get; set; } = new();
}
