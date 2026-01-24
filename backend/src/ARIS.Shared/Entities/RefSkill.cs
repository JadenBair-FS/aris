using Pgvector;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ARIS.Shared.Entities;

public class RefSkill
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("name")]
    public required string Name { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("source")]
    public string? Source { get; set; } // "ONET", "Roadmap", etc.

    [Column("embedding", TypeName = "vector(384)")]
    public Vector? Embedding { get; set; }
}
