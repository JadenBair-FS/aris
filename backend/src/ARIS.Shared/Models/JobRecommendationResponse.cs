namespace ARIS.Shared.Models
{
    public class JobRecommendationResponse
    {
        public List<JobMatchResult> Matches { get; set; } = [];
        public string Analysis { get; set; } = string.Empty;
    }
}
