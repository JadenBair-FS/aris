namespace ARIS.Shared.Models;

public class MatchRequest
{
    public Guid UserProfileId { get; set; }
    public Guid JobId { get; set; }
}

public class QuickMatchRequest
{
    public Guid UserProfileId { get; set; }
    public required string JobDescriptionText { get; set; }
}

public class ExplainMatchRequest
{
    public Guid UserProfileId { get; set; }
    public Guid JobId { get; set; }
}
