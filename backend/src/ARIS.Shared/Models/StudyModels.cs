namespace ARIS.Shared.Models;

public class StudyCompareRequest
{
    public required string ResumeText { get; set; }
    public required string JobDescriptionText { get; set; }
}

public class PrepareTextRequest
{
    public required string Text { get; set; }
}

public class PrepareSessionResponse
{
    public required string SessionKey { get; set; }
}

public class GenerateComparisonRequest
{
    public required string ResumeKey { get; set; }
    public required string JobKey { get; set; }
    public required string ResumeText { get; set; }
    public required string JobDescriptionText { get; set; }
}

public class StudyTailorRequest
{
    public string? ResumeKey { get; set; }
    public string? JobKey { get; set; }
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

public class StudyMatchPreviewRequest
{
    public string? ResumeKey { get; set; }
    public string? ResumeText { get; set; }
}

public class StudyMatchPreviewItem
{
    public string JobTitle { get; set; } = "";
    public string? CompanyName { get; set; }
    public double ArisScore { get; set; }
    public double VectorSimilarity { get; set; }
    public int T1Count { get; set; }
    public int T2Count { get; set; }
    public int T3Count { get; set; }
    public int T4Count { get; set; }
    public int T5Count { get; set; }
    public List<string> TopMatchingSkills { get; set; } = [];
    public List<string> TopMissingSkills { get; set; } = [];
}

public class StudyMatchPreviewResponse
{
    public List<StudyMatchPreviewItem> Matches { get; set; } = [];
    public int TotalJobsSearched { get; set; }
}

public class StudyAnalyzeRequest
{
    public required string ResumeText { get; set; }
    public required string JobDescriptionText { get; set; }
}

public class StudyAnalyzeResponse
{
    public double ArisScore { get; set; }
    public double VectorSimilarity { get; set; }
    public List<SkillGapItem> MatchingSkills { get; set; } = [];
    public List<SkillGapItem> ImplicitlyDiscoveredSkills { get; set; } = [];
    public List<SkillGapItem> PrerequisiteMetSkills { get; set; } = [];
    public List<SkillGapItem> BridgeableSkills { get; set; } = [];
    public List<SkillGapItem> HardGaps { get; set; } = [];
    public string ArisResume { get; set; } = string.Empty;
    public string ChatGptResume { get; set; } = string.Empty;
    public string SessionResumeKey { get; set; } = string.Empty;
    public string SessionJobKey { get; set; } = string.Empty;
}

public class StudyExplainRequest
{
    public string? ResumeKey { get; set; }
    public string? JobKey { get; set; }
    public required string ResumeText { get; set; }
    public required string JobDescriptionText { get; set; }
}

public class AnalyzeWithProfileRequest
{
    public Guid UserId { get; set; }
    public string JobDescriptionText { get; set; } = "";
}

public class StudyExplainResponse
{
    public string Explanation { get; set; } = string.Empty;
}

public class StudyTailorResumesRequest
{
    public required string SessionResumeKey { get; set; }
    public required string SessionJobKey { get; set; }
}

public class StudyTailorResumesResponse
{
    public string ArisResume { get; set; } = string.Empty;
    public string ChatGptResume { get; set; } = string.Empty;
}
