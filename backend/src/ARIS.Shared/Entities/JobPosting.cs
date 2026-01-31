using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ARIS.Shared.Models.CleanSignal;
using Pgvector;

namespace ARIS.Shared.Entities;

public class JobPosting
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [Column("recruiter_id")]
    public required string RecruiterId { get; set; } // External Auth ID

    [Column("raw_description", TypeName = "text")]
    public string? RawDescription { get; set; }

    [Column("clean_signal_json", TypeName = "jsonb")]
    public JobPostingCleanSignal? CleanSignal { get; set; }

    [Column("embedding", TypeName = "vector(384)")]
    public Vector? Embedding { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
