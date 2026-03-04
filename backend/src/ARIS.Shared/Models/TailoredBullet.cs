namespace ARIS.Shared.Models;

public class TailoredBullet
{
    public string OriginalBullet { get; set; } = "";
    public string RewrittenBullet { get; set; } = "";
    public string TargetSkill { get; set; } = "";
    public string Role { get; set; } = "";      // for PDF assembly — which experience entry this bullet belongs to
    public string Company { get; set; } = "";   // for PDF assembly — company name from ExperienceSummary
    public string? BridgePath { get; set; }
}
