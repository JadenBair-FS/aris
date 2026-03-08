using ARIS.Shared.Models;
using ARIS.Shared.Models.CleanSignal;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ARIS.API.Services;

public class ResumePdfService
{
    private const string Gray555 = "#555555";
    private const string Gray777 = "#777777";
    private const string GrayRule = "#cccccc";

    /// <summary>
    /// Generates a full ATS-optimized single-column PDF resume.
    /// Layout: Header → Summary → Skills (by category) → Experience → Education
    /// </summary>
    public byte[] GeneratePdf(
        PersonalInfo info,
        ResumeCleanSignal cleanSignal,
        string professionalSummary,
        List<TailoredBullet> tailoredBullets)
    {
        // Build lookup: role → (original bullet → rewritten bullet)
        var bulletMap = tailoredBullets
            .Where(b => !string.IsNullOrWhiteSpace(b.Role))
            .GroupBy(b => b.Role, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(
                    b => b.OriginalBullet,
                    b => b.RewrittenBullet,
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(0.75f, Unit.Inch);
                page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(10).FontColor(Colors.Black));

                page.Content().Column(col =>
                {
                    // Header
                    if (!string.IsNullOrWhiteSpace(info.Name))
                        col.Item().Text(info.Name).FontSize(16).Bold();

                    var contactParts = new List<string>();
                    if (!string.IsNullOrEmpty(info.Email)) contactParts.Add(info.Email);
                    if (!string.IsNullOrEmpty(info.Phone)) contactParts.Add(info.Phone);
                    if (!string.IsNullOrEmpty(info.Location)) contactParts.Add(info.Location);
                    if (contactParts.Count > 0)
                        col.Item().PaddingTop(2).Text(string.Join("  |  ", contactParts)).FontSize(9).FontColor(Gray555);

                    var linkParts = new List<string>();
                    if (!string.IsNullOrEmpty(info.LinkedIn)) linkParts.Add(info.LinkedIn);
                    if (!string.IsNullOrEmpty(info.GitHub)) linkParts.Add(info.GitHub);
                    if (!string.IsNullOrEmpty(info.Website)) linkParts.Add(info.Website);
                    if (linkParts.Count > 0)
                        col.Item().Text(string.Join("  |  ", linkParts)).FontSize(9).FontColor(Gray555);

                    col.Item().PaddingTop(10);

                    //Summary 
                    if (!string.IsNullOrWhiteSpace(professionalSummary))
                    {
                        RenderSectionHeader(col, "SUMMARY");
                        col.Item().PaddingTop(5)
                            .Text(professionalSummary)
                            .FontSize(10)
                            .LineHeight(1.45f);
                        col.Item().PaddingTop(14);
                    }

                    // Skills
                    var groundedGroups = cleanSignal.Skills
                        .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                        .GroupBy(s => string.IsNullOrWhiteSpace(s.Category) ? "Other" : s.Category)
                        .OrderBy(g => g.Key)
                        .ToList();

                    var hasSkills = groundedGroups.Count > 0 || cleanSignal.UngroundedSkills.Count > 0;
                    if (hasSkills)
                    {
                        RenderSectionHeader(col, "SKILLS");
                        col.Item().PaddingTop(4);

                        foreach (var group in groundedGroups)
                        {
                            var names = string.Join(", ", group.Select(s => s.Name));
                            col.Item().PaddingTop(2).Text(t =>
                            {
                                t.Span($"{group.Key}: ").Bold().FontSize(9);
                                t.Span(names).FontSize(9);
                            });
                        }

                        if (cleanSignal.UngroundedSkills.Count > 0)
                        {
                            var names = string.Join(", ", cleanSignal.UngroundedSkills.Select(s => s.Name));
                            col.Item().PaddingTop(2).Text(t =>
                            {
                                t.Span("Additional: ").Bold().FontSize(9);
                                t.Span(names).FontSize(9);
                            });
                        }

                        col.Item().PaddingTop(14);
                    }

                    // Experience
                    if (cleanSignal.ExperienceSummary.Count > 0)
                    {
                        RenderSectionHeader(col, "EXPERIENCE");
                        col.Item().PaddingTop(4);

                        foreach (var exp in cleanSignal.ExperienceSummary)
                        {
                            col.Item().PaddingTop(8).Text(t =>
                            {
                                t.Span(exp.Role).Bold();
                                if (!string.IsNullOrEmpty(exp.Company))
                                    t.Span($"  –  {exp.Company}").FontColor(Gray555);
                            });

                            var matchingRole = cleanSignal.Roles.FirstOrDefault(r =>
                                r.Title.Contains(exp.Role, StringComparison.OrdinalIgnoreCase) ||
                                exp.Role.Contains(r.Title, StringComparison.OrdinalIgnoreCase));

                            if (matchingRole != null && !string.IsNullOrEmpty(matchingRole.Duration))
                                col.Item().Text(matchingRole.Duration).FontSize(9).FontColor(Gray777);

                            var roleMap = bulletMap.TryGetValue(exp.Role, out var bm) ? bm : null;
                            foreach (var bullet in exp.Bullets.Where(b => !string.IsNullOrWhiteSpace(b)))
                            {
                                var display = roleMap != null && roleMap.TryGetValue(bullet, out var rewritten)
                                    ? rewritten
                                    : bullet;
                                col.Item().PaddingLeft(14).PaddingTop(2).Text($"- {display}").FontSize(10).LineHeight(1.3f);
                            }
                        }

                        col.Item().PaddingTop(14);
                    }

                    // Education
                    if (cleanSignal.Education.Count > 0)
                    {
                        RenderSectionHeader(col, "EDUCATION");
                        col.Item().PaddingTop(4);

                        foreach (var edu in cleanSignal.Education)
                        {
                            col.Item().PaddingTop(8).Text(t =>
                            {
                                t.Span(edu.Degree).Bold();
                                if (!string.IsNullOrEmpty(edu.Institution))
                                    t.Span($"  –  {edu.Institution}");
                                if (!string.IsNullOrEmpty(edu.Year))
                                    t.Span($"  ({edu.Year})").FontColor(Gray777);
                            });
                        }
                    }
                });
            });
        });

        return document.GeneratePdf();
    }

    private static void RenderSectionHeader(ColumnDescriptor col, string title)
    {
        col.Item().Text(title).FontSize(11).Bold();
        col.Item().Height(1).Background(GrayRule);
    }
}
