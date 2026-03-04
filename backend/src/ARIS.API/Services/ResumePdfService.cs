using ARIS.Shared.Models;
using ARIS.Shared.Models.CleanSignal;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ARIS.API.Services;

public class ResumePdfService
{
    /// <summary>
    /// Generates an ATS-safe single-column PDF resume.
    /// Personal info comes from PersonalInfoExtractor.
    /// Skills and experience structure come from the Clean Signal.
    /// Bullets are replaced with tailored versions where available.
    /// </summary>
    public byte[] GeneratePdf(
        PersonalInfo info,
        ResumeCleanSignal cleanSignal,
        List<TailoredBullet> tailoredBullets)
    {
        // Build lookup: role name -> (original bullet -> rewritten bullet)
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
                    // ── Personal Info Header ───────────────────────────────────
                    if (!string.IsNullOrEmpty(info.Name))
                    {
                        col.Item().Text(info.Name).FontSize(14).Bold();
                    }

                    var contactParts = new List<string>();
                    if (!string.IsNullOrEmpty(info.Email)) contactParts.Add(info.Email);
                    if (!string.IsNullOrEmpty(info.Phone)) contactParts.Add(info.Phone);
                    if (!string.IsNullOrEmpty(info.Location)) contactParts.Add(info.Location);
                    if (contactParts.Count > 0)
                        col.Item().Text(string.Join("  |  ", contactParts)).FontSize(9).FontColor("#555555");

                    var linkParts = new List<string>();
                    if (!string.IsNullOrEmpty(info.LinkedIn)) linkParts.Add(info.LinkedIn);
                    if (!string.IsNullOrEmpty(info.GitHub)) linkParts.Add(info.GitHub);
                    if (!string.IsNullOrEmpty(info.Website)) linkParts.Add(info.Website);
                    if (linkParts.Count > 0)
                        col.Item().Text(string.Join("  |  ", linkParts)).FontSize(9).FontColor("#555555");

                    col.Item().PaddingTop(10);

                    // ── Skills Section ─────────────────────────────────────────
                    if (cleanSignal.Skills.Count > 0)
                    {
                        col.Item().Text("SKILLS").FontSize(11).Bold();
                        col.Item().Height(1).Background("#cccccc");
                        col.Item().Height(4);
                        col.Item().PaddingTop(4)
                            .Text(string.Join("  -  ", cleanSignal.Skills.Select(s => s.Name)))
                            .FontSize(9);
                        col.Item().PaddingTop(14);
                    }

                    // ── Experience Section ─────────────────────────────────────
                    if (cleanSignal.ExperienceSummary.Count > 0)
                    {
                        col.Item().Text("EXPERIENCE").FontSize(11).Bold();
                        col.Item().Height(1).Background("#cccccc");
                        col.Item().Height(4);

                        foreach (var exp in cleanSignal.ExperienceSummary)
                        {
                            col.Item().PaddingTop(8).Text(t =>
                            {
                                t.Span(exp.Role).Bold();
                                if (!string.IsNullOrEmpty(exp.Company))
                                    t.Span($"  -  {exp.Company}").FontColor("#555555");
                            });

                            // Look up duration from ResumeRoles by title match
                            var matchingRole = cleanSignal.Roles.FirstOrDefault(r =>
                                r.Title.Contains(exp.Role, StringComparison.OrdinalIgnoreCase) ||
                                exp.Role.Contains(r.Title, StringComparison.OrdinalIgnoreCase));

                            if (matchingRole != null && !string.IsNullOrEmpty(matchingRole.Duration))
                                col.Item().Text(matchingRole.Duration).FontSize(9).FontColor("#777777");

                            var roleMap = bulletMap.TryGetValue(exp.Role, out var bm) ? bm : null;
                            foreach (var bullet in exp.Bullets.Where(b => !string.IsNullOrWhiteSpace(b)))
                            {
                                var display = roleMap != null && roleMap.TryGetValue(bullet, out var rewritten)
                                    ? rewritten
                                    : bullet;
                                col.Item().PaddingLeft(14).PaddingTop(2).Text($"- {display}");
                            }
                        }

                        col.Item().PaddingTop(14);
                    }

                    // ── Education Section ──────────────────────────────────────
                    if (cleanSignal.Education.Count > 0)
                    {
                        col.Item().Text("EDUCATION").FontSize(11).Bold();
                        col.Item().Height(1).Background("#cccccc");
                        col.Item().Height(4);

                        foreach (var edu in cleanSignal.Education)
                        {
                            col.Item().PaddingTop(8).Text(t =>
                            {
                                t.Span(edu.Degree).Bold();
                                if (!string.IsNullOrEmpty(edu.Institution))
                                    t.Span($"  -  {edu.Institution}");
                                if (!string.IsNullOrEmpty(edu.Year))
                                    t.Span($"  ({edu.Year})").FontColor("#777777");
                            });
                        }
                    }
                });
            });
        });

        return document.GeneratePdf();
    }
}
