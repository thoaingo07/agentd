using System.Globalization;
using System.Text;

namespace Agentd.Application.Reviews;

/// <summary>
/// What a review sends (docs/architect/review-sessions.md §4): the kept and edited findings, worst first, then the
/// person's comments, each with its place. The same text for an agent (<c>.agentd/review.md</c>) and for "Copy as text".
/// </summary>
public static class ReviewFeedback
{
    /// <summary>The findings that go out: dropped ones stay home; an edited one carries the person's words.</summary>
    public static IReadOnlyList<ReviewFinding> Kept(IEnumerable<SessionFinding> findings) =>
        [.. findings.Where(f => f.Decision != FindingDecisions.Dropped).Select(f => f.Decision == FindingDecisions.Edited && !string.IsNullOrWhiteSpace(f.Edited)
            ? new ReviewFinding(f.Severity, f.File, f.Line, f.Title, f.Edited.Trim(), null)
            : new ReviewFinding(f.Severity, f.File, f.Line, f.Title, f.Detail, f.Suggestion))];

    public static string Render(string comparedWith, string? summary, IEnumerable<SessionFinding> findings, IEnumerable<ReviewComment> comments)
    {
        var sb = new StringBuilder().AppendLine("# Review findings (agentd review)").AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Compared with {comparedWith}. {summary}").TrimEnd()).AppendLine();
        var kept = Kept(findings);
        var notes = comments.ToList();
        if (kept.Count == 0 && notes.Count == 0)
        {
            return sb.AppendLine("Nothing to fix: every finding was dropped.").ToString();
        }

        sb.AppendLine("Fix these, then run `agentd review` again:").AppendLine();
        var n = 0;
        foreach (var f in kept)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{++n}. {(f.Severity == ReviewFindings.Breaks ? "🔴" : "🟠")} **{f.Title}**{At(f.File, f.Line, " · ")}");
            if (!string.IsNullOrWhiteSpace(f.Detail))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"   {f.Detail.Trim().ReplaceLineEndings(" ")}");
            }

            if (!string.IsNullOrWhiteSpace(f.Suggestion))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"   Fix: {f.Suggestion.Trim().ReplaceLineEndings(" ")}");
            }
        }

        foreach (var c in notes)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{++n}. 💬 On {(c.File is null ? "the whole change" : At(c.File, c.Line, string.Empty))}: {c.Text.Trim().ReplaceLineEndings(" ")}");
        }

        return sb.ToString();
    }

    private static string At(string? file, int? line, string prefix) =>
        file is null ? string.Empty : $"{prefix}`{file}{(line is { } l ? string.Create(CultureInfo.InvariantCulture, $":{l}") : string.Empty)}`";
}
