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
            return ReviewFeedback.Render(baseLabel, result.Summary, _findings, _comments);
        }
    }
}
