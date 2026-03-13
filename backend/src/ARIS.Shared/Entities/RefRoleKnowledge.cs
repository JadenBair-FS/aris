using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ARIS.Shared.Entities;

[PrimaryKey(nameof(RoleId), nameof(KnowledgeId))]
public class RefRoleKnowledge
{
    [Column("role_id")]
    public int RoleId { get; set; }
    [ForeignKey(nameof(RoleId))]
    public RefRole Role { get; set; } = null!;

    [Column("knowledge_id")]
    public int KnowledgeId { get; set; }
    [ForeignKey(nameof(KnowledgeId))]
    public RefKnowledge Knowledge { get; set; } = null!;

    [Column("importance")]
    public int? Importance { get; set; }
}
