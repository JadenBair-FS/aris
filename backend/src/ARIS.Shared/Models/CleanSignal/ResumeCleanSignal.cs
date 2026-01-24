using System.Text.Json.Serialization;

namespace ARIS.Shared.Models.CleanSignal
{

    public class ResumeCleanSignal
    {
        [JsonPropertyName("roles")]
        public List<ResumeRole> Roles { get; set; } = [];

        [JsonPropertyName("skills")]
        public List<ResumeSkill> Skills { get; set; } = [];

        [JsonPropertyName("experience_summary")]
        public List<ExperienceSummary> ExperienceSummary { get; set; } = [];

        [JsonPropertyName("education")]
        public List<Education> Education { get; set; } = [];
    }

    public class ResumeRole
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;
    
        [JsonPropertyName("duration")]
        public string Duration { get; set; } = string.Empty;
    
        [JsonPropertyName("is_current")]
        public string IsCurrent { get; set; } = string.Empty;
    }
    public class ResumeSkill
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("proficiency")]
        public string Proficiency { get; set; } = string.Empty;
    }

    public class ExperienceSummary
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("company")]
        public string Company { get; set; } = string.Empty;

        [JsonPropertyName("bullets")]
        public List<string> Bullets { get; set; } = [];

    }
    public class Education
    {
        [JsonPropertyName("degree")]
        public string Degree { get; set; } = string.Empty;

        [JsonPropertyName("institution")]
        public string Institution { get; set; } = string.Empty;

        [JsonPropertyName("year")]
        public string Year { get; set; } = string.Empty;
    }
}

