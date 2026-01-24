using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ARIS.Shared.Models;
using ARIS.Shared.Models.CleanSignal;
using Pgvector;

namespace ARIS.Shared.Entities;

public class UserProfile
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [Column("user_id")]
    public required string UserId { get; set; } // External Auth ID (e.g., Clerk)

    [Column("raw_resume", TypeName = "jsonb")]
    public string? RawResume { get; set; } // Storing parsed text/structure if needed

    [Column("clean_signal_json", TypeName = "jsonb")]
    public ResumeCleanSignal? CleanSignal { get; set; }

    [Column("embedding", TypeName = "vector(384)")]
    public Vector? Embedding { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
