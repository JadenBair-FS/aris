namespace ARIS.Shared.Models;

public class SiblingCluster
{
    public string Parent { get; set; } = "";
    public List<string> Siblings { get; set; } = new();
}

public class RoleSkillCluster
{
    public string RoleTitle { get; set; } = "";
    public List<string> Skills { get; set; } = new();
}