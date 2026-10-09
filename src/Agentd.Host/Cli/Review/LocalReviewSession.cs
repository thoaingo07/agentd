using System.Globalization;
using System.Text;
using Agentd.Application.Reviews;

namespace Agentd.Host.Cli.Review;

/// <summary>
/// One local review (<c>agentd review</c>'s page): the reviewer's findings with the developer's keep / edit / drop, their
/// comments, and what Send writes for their agent. In memory: it lives as long as the command.
/// </summary>
internal sealed class LocalReviewSession(string repository, string baseLabel, string baseCommit, string diff, IReadOnlyList<string> files, ReviewResult result, string author)
{
    public const long Id = 1;

    private readonly Lock _gate = new();
    private readonly List<SessionFinding> _findings = [.. result.Findings.Select(SessionFinding.From)];
    private readonly List<ReviewComment> _comments = [];
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private long _nextComment;
    private bool _sent;

    public string Diff { get; } = diff;

    public IReadOnlyList<string> Files { get; } = files;

    public string BaseCommit { get; } = baseCommit;

    public ReviewSessionView View()
    {
        lock (_gate)
        {
            var session = new ReviewSession(Id, repository, ReviewTargets.Upload, null, "your changes", baseLabel, BaseCommit, "working tree",
                _sent ? ReviewSessionStatus.Sent : ReviewSessionStatus.Ready, null, null, null, result.Summary, [.. _findings], null, null, author, _sent ? ReviewFile : null,
                _started, DateTimeOffset.UtcNow);
            return new ReviewSessionView(session, [.. _comments], []);
        }
    }

    /// <summary>Keep / drop / edit finding <paramref name="number"/> (1-based); false when there's none or it's sent.</summary>
    public bool Decide(int number, string decision, string? edited)
    {
        lock (_gate)
        {
            if (_sent || number < 1 || number > _findings.Count)
            {
                return false;
            }

            _findings[number - 1] = _findings[number - 1] with { Decision = decision, Edited = decision == FindingDecisions.Edited ? edited : null };
            return true;
        }
    }

    public ReviewComment? Comment(string? file, int? line, int? endLine, string text)
    {
        lock (_gate)
        {
            if (_sent)
            {
                return null;
            }

            var comment = new ReviewComment(++_nextComment, file, line, endLine, text, author, DateTimeOffset.UtcNow);
            _comments.Add(comment);
            return comment;
        }
    }

    public bool DeleteComment(long id)
    {
        lock (_gate)
        {
            return !_sent && _comments.RemoveAll(c => c.Id == id) > 0;
        }
    }

    public const string ReviewFile = ".agentd/review.md";

    /// <summary>Marks it sent and returns what goes to the agent: the kept and edited findings, worst first, then the comments.</summary>
    public string Send()
    {
        lock (_gate)
        {
            _sent = true;
            var sb = new StringBuilder().AppendLine("# Review findings (agentd review)").AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Compared with {baseLabel}. {result.Summary}").AppendLine();
            var kept = _findings.Where(f => f.Decision != FindingDecisions.Dropped).ToList();
            if (kept.Count == 0 && _comments.Count == 0)
            {
                return sb.AppendLine("Nothing to fix: every finding was dropped.").ToString();
            }

            sb.AppendLine("Fix these, then run `agentd review` again:").AppendLine();
            var n = 0;
            foreach (var f in kept)
            {
                var where = f.File is null ? string.Empty : $" · `{f.File}{(f.Line is { } l ? string.Create(CultureInfo.InvariantCulture, $":{l}") : string.Empty)}`";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{++n}. {(f.Severity == ReviewFindings.Breaks ? "🔴" : "🟠")} **{f.Title}**{where}");
                if (f.Decision == FindingDecisions.Edited && !string.IsNullOrWhiteSpace(f.Edited))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"   {f.Edited.Trim().ReplaceLineEndings(" ")}");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(f.Detail))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"   {f.Detail.Trim().ReplaceLineEndings(" ")}");
                }

                if (!string.IsNullOrWhiteSpace(f.Suggestion))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"   Fix: {f.Suggestion.Trim().ReplaceLineEndings(" ")}");
                }
            }

            foreach (var c in _comments)
            {
                var where = c.File is null ? "the whole change" : $"`{c.File}{(c.Line is { } l ? string.Create(CultureInfo.InvariantCulture, $":{l}") : string.Empty)}`";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{++n}. 💬 On {where}: {c.Text.Trim().ReplaceLineEndings(" ")}");
            }

            return sb.ToString();
        }
    }
}
