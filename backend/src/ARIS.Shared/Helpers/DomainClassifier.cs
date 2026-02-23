namespace ARIS.Shared.Helpers;

public static class DomainClassifier
{
    private static readonly HashSet<string> TechPrefixes = new() { "15" };

    /// <summary>
    /// Determines if an O*NET code belongs to a tech domain (SOC major group 15
    /// or Roadmap.sh-derived roles). Non-tech domains should not see Roadmap.sh
    /// skills during grounding or graph traversal.
    /// </summary>
    public static bool IsTechDomain(string? onetCode, string? roleTitle = null)
    {
        if (!string.IsNullOrEmpty(onetCode))
        {
            if (onetCode.StartsWith("ROADMAP_")) return true;
            var prefix = onetCode.Split('-')[0];
            if (TechPrefixes.Contains(prefix)) return true;
        }

        if (!string.IsNullOrEmpty(roleTitle))
        {
            var t = roleTitle.ToLowerInvariant();
            if (t.Contains("software") || t.Contains("developer") || t.Contains("engineer") || 
                t.Contains("data") || t.Contains("web") || t.Contains("cloud") || t.Contains("devops") ||
                t.Contains("programmer") || t.Contains("coder") || t.Contains("analyst"))
            {
                return true;
            }
        }

        return false;
    }
}
