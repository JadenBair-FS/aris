using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pgvector;

namespace ARIS.Shared.Entities;

public class CandidateSkill
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Original text extracted by the LLM.</summary>
    [Required]
    [Column("name", TypeName = "text")]
    public required string Name { get; set; }

    /// <summary>Lowercased + trimmed, used for deduplication.</summary>
    [Required]
    [Column("normalized_name", TypeName = "text")]
    public required string NormalizedName { get; set; }

    /// <summary>O*NET code prefix (e.g. "15") derived from the document's primary role. Null = cross-domain.</summary>
    [Column("domain_prefix", TypeName = "text")]
    public string? DomainPrefix { get; set; }

    [Column("is_tech")]
    public bool IsTech { get; set; }

    [Column("embedding", TypeName = "vector(1024)")]
    public Vector? Embedding { get; set; }

    /// <summary>Number of unique source documents that independently extracted this skill.</summary>
    [Column("observation_count")]
    public int ObservationCount { get; set; } = 1;

    /// <summary>IDs of source documents (resume profile IDs or job posting IDs) that observed this skill.
    /// Used to prevent double-counting the same document.</summary>
    [Column("source_document_ids", TypeName = "text[]")]
    public string[] SourceDocumentIds { get; set; } = [];

    /// <summary>candidate | promoted | rejected</summary>
    [Column("status", TypeName = "text")]
    public string Status { get; set; } = "candidate";

    /// <summary>FK to ref_skills.id once promoted. Null while still a candidate.</summary>
    [Column("promoted_skill_id")]
    public int? PromotedSkillId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
