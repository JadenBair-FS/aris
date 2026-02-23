namespace ARIS.Shared.Models;

public class MatchAnalysisResult
{
    public Guid JobId { get; set; }
    /// <summary>Raw cosine similarity between user and job embeddings (Qwen3 via pgvector).</summary>
    public double VectorSimilarity { get; set; }
    /// <summary>
    /// ARIS blended score: 0.7 × VectorSimilarity + 0.3 × GraphCoverageScore.
    /// GraphCoverageScore = (match×1.0 + implicit×0.8 + prereqMet×0.6 + bridgeable×0.4) / totalJobSkills.
    /// Incorporates knowledge-graph evidence to correct embedding-space ranking failures.
    /// </summary>
    public double ArisScore { get; set; }
    public List<string> MatchingSkills { get; set; } = new();              // Tier 1: direct matches
    public List<string> ImplicitlyDiscoveredSkills { get; set; } = new(); // Tier 2: auto-granted via UP traversal
    public List<SkillGapItem> PrerequisiteMetSkills { get; set; } = new(); // Tier 3: user has foundation/parent
    public List<SkillGapItem> BridgeableSkills { get; set; } = new();      // Tier 4: reachable via graph bridge
    public List<SkillGapItem> HardGaps { get; set; } = new();              // Tier 5: true gaps (certs, no path)
}
