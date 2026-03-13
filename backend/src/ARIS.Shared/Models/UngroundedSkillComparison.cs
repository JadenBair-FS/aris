namespace ARIS.Shared.Models;

public class UngroundedSkillComparison
{
    /// <summary>Skills present in both resume and job posting (normalized name match).</summary>
    public List<string> Matched { get; set; } = [];

    /// <summary>In job posting but not in resume (candidate is missing these).</summary>
    public List<string> MissingFromResume { get; set; } = [];

    /// <summary>In resume but not in job posting (extra skills the candidate has).</summary>
    public List<string> ExtraInResume { get; set; } = [];
}
