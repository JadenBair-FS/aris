using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ARIS.Shared.Entities;

[PrimaryKey(nameof(RoleId), nameof(AbilityId))]
public class RefRoleAbility
{
    [Column("role_id")]
    public int RoleId { get; set; }
    [ForeignKey(nameof(RoleId))]
    public RefRole Role { get; set; } = null!;

    [Column("ability_id")]
    public int AbilityId { get; set; }
    [ForeignKey(nameof(AbilityId))]
    public RefAbility Ability { get; set; } = null!;

    [Column("importance")]
    public int? Importance { get; set; }
}
