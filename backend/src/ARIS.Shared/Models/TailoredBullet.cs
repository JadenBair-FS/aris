namespace ARIS.Shared.Models;

public class TailoredBullet
{
    public string OriginalBullet { get; set; } = "";
    public string RewrittenBullet { get; set; } = "";
    public string TargetSkill { get; set; } = "";
    public string? BridgePath { get; set; }
}
