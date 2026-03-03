namespace ARIS.Shared.Models
{
    public class CreateJobRequest
    {
        public required string Description { get; set; }
        // Deprecated: identity is now sourced from the JWT sub claim.
        // Retained as optional fallback for bulk upload scripts that lack a JWT.
        public string? RecruiterId { get; set; }
    }

}
