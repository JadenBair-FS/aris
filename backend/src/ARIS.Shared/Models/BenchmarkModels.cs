namespace ARIS.Shared.Models;

public class ExtractionBenchmarkRequest
{
    public string Text { get; set; } = "";
    public string Type { get; set; } = "job";          // "job" | "resume"
    public int Runs { get; set; } = 3;
    public List<string> Models { get; set; } = ["mistral", "llama3.2:3b"];
}

public class ModelBenchmarkResult
{
    public List<long> LatencyMs { get; set; } = [];
    public double AvgLatencyMs { get; set; }
    public object? Output { get; set; }                // parsed CleanSignal
    public int SkillCount { get; set; }
    public int RoleCount { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class ExtractionBenchmarkResponse
{
    public string Type { get; set; } = "";
    public int Runs { get; set; }
    public Dictionary<string, ModelBenchmarkResult> Results { get; set; } = [];
    public BenchmarkComparison Comparison { get; set; } = new();
}

public class BenchmarkComparison
{
    public string FasterModel { get; set; } = "";
    public double SpeedupFactor { get; set; }          // slowerAvg / fasterAvg
    public Dictionary<string, int> SkillCounts { get; set; } = [];
    public List<string> SharedSkills { get; set; } = [];
    public List<string> UniqueToLargerModel { get; set; } = [];  // skills only in Mistral
}
