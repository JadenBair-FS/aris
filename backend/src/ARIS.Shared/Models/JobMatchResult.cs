using ARIS.Shared.Entities;

namespace ARIS.Shared.Models
{
    public class JobMatchResult
    {
        public Guid JobId { get; set; }
        public JobPosting? Job { get; set; }
        public double Score { get; set; }
        public double Distance { get; set; }
    }
}
