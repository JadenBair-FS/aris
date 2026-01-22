using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ARIS.Shared.Entities;

[PrimaryKey(nameof(RoleId), nameof(SkillId))]
public class RefRoleSkill
{
    [Column("role_id")]
    public int RoleId { get; set; }
    [ForeignKey(nameof(RoleId))]
    public RefRole Role { get; set; } = null!;

    [Column("skill_id")]
    public int SkillId { get; set; }
    [ForeignKey(nameof(SkillId))]
    public RefSkill Skill { get; set; } = null!;
    
    // Optional: Relevance score or level from O*NET
    [Column("importance")]
    public double? Importance { get; set; }
    [Column("level")]
    public double? Level { get; set; }
}
