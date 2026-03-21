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
        // Group all rewritten bullets by role; deduplicate by rewritten text.
        // One original bullet may produce multiple rewrites (one per surfaced skill),
        // so we render all distinct rewrites rather than doing a 1-to-1 lookup.
        var tailoredByRole = tailoredBullets
            .Where(b => !string.IsNullOrWhiteSpace(b.Role) && !string.IsNullOrWhiteSpace(b.RewrittenBullet))
            .GroupBy(b => b.Role, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(b => b.RewrittenBullet)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToList(),
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

                    if (!string.IsNullOrWhiteSpace(professionalSummary))
                    {
                        RenderSectionHeader(col, "SUMMARY");
                        col.Item().PaddingTop(5)
                            .Text(professionalSummary)
                            .FontSize(10)
                            .LineHeight(1.45f);
                        col.Item().PaddingTop(14);
                    }

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

                            var dateRange = "";
                            if (!string.IsNullOrEmpty(exp.StartDate) || !string.IsNullOrEmpty(exp.EndDate))
                            {
                                dateRange = !string.IsNullOrEmpty(exp.StartDate) && !string.IsNullOrEmpty(exp.EndDate)
                                    ? $"{exp.StartDate} - {exp.EndDate}"
                                    : !string.IsNullOrEmpty(exp.StartDate) ? exp.StartDate : exp.EndDate;
                            }
                            else
                            {
                                var matchingRole = cleanSignal.Roles.FirstOrDefault(r =>
                                    r.Title.Contains(exp.Role, StringComparison.OrdinalIgnoreCase) ||
                                    exp.Role.Contains(r.Title, StringComparison.OrdinalIgnoreCase));
                                if (matchingRole != null && !string.IsNullOrEmpty(matchingRole.Duration))
                                    dateRange = matchingRole.Duration;
                            }

                            if (!string.IsNullOrEmpty(dateRange))
                                col.Item().Text(dateRange).FontSize(9).FontColor(Gray777);

                            if (tailoredByRole.TryGetValue(exp.Role, out var rewrittenBullets) && rewrittenBullets.Count > 0)
                            {
                                foreach (var rb in rewrittenBullets)
                                    col.Item().PaddingLeft(14).PaddingTop(2).Text($"- {rb}").FontSize(10).LineHeight(1.3f);
                            }
                            else
                            {
                                foreach (var bullet in exp.Bullets.Where(b => !string.IsNullOrWhiteSpace(b)))
                                    col.Item().PaddingLeft(14).PaddingTop(2).Text($"- {bullet}").FontSize(10).LineHeight(1.3f);
                            }
                        }

                        col.Item().PaddingTop(14);
                    }

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

    /// <summary>
    /// Generates a simple plain-text PDF (for study/comparison use). Detects section headers
    /// (short all-caps lines) and bullet points for basic formatting.
    /// </summary>
    public byte[] GeneratePlainTextPdf(string text)
    {
        var lines = text.Split('\n');
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(0.75f, Unit.Inch);
                page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(10).FontColor(Colors.Black));

                page.Content().Column(col =>
                {
                    foreach (var raw in lines)
                    {
                        var line = raw.TrimEnd();
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            col.Item().Height(5);
                            continue;
                        }

                        var trimmed = line.Trim();
                        var upper = trimmed.ToUpperInvariant();
                        var isHeader = trimmed.Length > 1
                            && trimmed.Length <= 60
                            && trimmed == upper
                            && !trimmed.StartsWith('-')
                            && !trimmed.StartsWith('•');

                        if (isHeader)
                        {
                            col.Item().PaddingTop(10).Text(trimmed).FontSize(11).Bold();
                            col.Item().Height(1).Background(GrayRule);
                            col.Item().Height(3);
                        }
                        else if (trimmed.StartsWith('-') || trimmed.StartsWith('•'))
                        {
                            col.Item().PaddingLeft(14).PaddingTop(2)
                                .Text(trimmed).FontSize(10).LineHeight(1.3f);
                        }
                        else
                        {
                            var indent = line.Length - line.TrimStart().Length;
                            col.Item().PaddingLeft(indent > 0 ? 14 : 0).PaddingTop(2)
                                .Text(trimmed).FontSize(10).LineHeight(1.35f);
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
