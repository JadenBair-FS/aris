using Pgvector;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ARIS.Shared.Entities;

public class RefKnowledge
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("onet_id")]
    public required string OnetId { get; set; }

    [Required]
    [Column("name")]
    public required string Name { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("embedding", TypeName = "vector(1024)")]
    public Vector? Embedding { get; set; }

    public ICollection<RefRoleKnowledge> RoleKnowledge { get; set; } = new List<RefRoleKnowledge>();
}
