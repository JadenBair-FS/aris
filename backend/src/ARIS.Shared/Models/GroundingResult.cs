namespace ARIS.Shared.Models;

public class GroundingResult
{
    public double Score { get; set; }
    public List<string> Hallucinations { get; set; } = [];
    public int TotalEntitiesFound { get; set; }
    public int ValidNeighborhoodSize { get; set; }
}