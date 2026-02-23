namespace ARIS.Shared.Models;

/// <summary>
/// Enriched gap item returned by MatchAnalysisResult for non-direct-match tiers.
/// Carries importance, years-required, and bridge path context for frontend rendering.
/// </summary>
public class SkillGapItem
{
    public string SkillName { get; set; } = "";
    public string Importance { get; set; } = "";      // "Essential" | "Preferred"
    public double YearsRequired { get; set; }
    public string? BridgePath { get; set; }           // e.g. "via MySQL (BRIDGE_TO)"
    public string? BridgeSource { get; set; }         // "Roadmap.sh" | "OntologyEnrichment" | null
}
