namespace ARIS.Shared.Models;

public class StudyCompareRequest
{
    public required string ResumeText { get; set; }
    public required string JobDescriptionText { get; set; }
}

public class StudyTierSummary
{
    public int Tier1Count { get; set; }
    public int Tier2Count { get; set; }
    public int Tier3Count { get; set; }
    public int Tier4Count { get; set; }
    public double VectorSimilarity { get; set; }
}

public class StudyCompareResponse
{
    public string RagResponse { get; set; } = string.Empty;
    public string GraphRagResponse { get; set; } = string.Empty;
    public StudyTierSummary TierSummary { get; set; } = new();
}

public class StudyTailorResponse
{
    public string TailoredText { get; set; } = string.Empty;
}
