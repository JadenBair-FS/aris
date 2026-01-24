using Pgvector;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ARIS.Shared.Entities;

public class RefRole
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("title")]
    public required string Title { get; set; }

    [Column("onet_code")]
    public string? OnetCode { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("embedding", TypeName = "vector(384)")]
    public Vector? Embedding { get; set; }

    public List<RefRoleSkill> RoleSkills { get; set; } = new();
}
