using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agentd.Application.Ideas;
using Agentd.Domain.Messaging;

namespace Agentd.Application.Reviews;

/// <summary>Reviewing → Reviewed → Posted | Kept | Discarded → Closed.</summary>
public static class ReviewStatus
{
    public const string Reviewing = "Reviewing";
    public const string Reviewed = "Reviewed";
    public const string Posted = "Posted";
    public const string Kept = "Kept";
    public const string Discarded = "Discarded";
    public const string Closed = "Closed";
}

/// <summary>
/// A pull request reviewed in a chat thread (<c>!review</c>): a read-only agent session on a detached checkout of the
/// PR head, whose findings stay in the thread until a person chooses to post them to the PR.
/// </summary>
public sealed record Review(
    long Id,
    string Repository,
    int PullRequestId,
    string Title,
    string Author,
    ProviderKey Provider,
    string ThreadId,
    string? SpaceId,
    string Status,
    string? HeadCommit,
    string? Focus,
    Guid? Session,
    string? Model,
    string? Effort,
    string? Worktree,
    ReviewResult? Result,
    IReadOnlyList<int> PostedThreads);

/// <summary>
/// The agent's latest findings and its summary of the PR. Once posted, <paramref name="MainThread"/> is the PR thread
/// with the main message (every finding and its status), edited in place.
/// </summary>
public sealed record ReviewResult(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("findings")] IReadOnlyList<ReviewFinding> Findings,
    [property: JsonPropertyName("mainThread")] int? MainThread = null,
    [property: JsonPropertyName("repliesSeenUntil")] DateTimeOffset? RepliesSeenUntil = null);

/// <summary>One posted finding after a re-check: <paramref name="Number"/> is its 1-based place, <paramref name="Reply"/> what to say on its thread (null: nothing new).</summary>
public sealed record RecheckItem(
    [property: JsonPropertyName("n")] int Number,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reply")] string? Reply);

/// <summary>A re-check of posted findings against the PR's new head, plus problems the new commits introduce.</summary>
public sealed record Recheck(
    [property: JsonPropertyName("findings")] IReadOnlyList<RecheckItem> Findings,
    [property: JsonPropertyName("new")] IReadOnlyList<ReviewFinding>? New);

/// <summary>
/// One finding: where (<paramref name="File"/> relative to the repo root, optional line on the PR's side), what breaks
/// or slows down, and how to fix it. Once posted, <paramref name="Thread"/> is its PR thread and <paramref name="Status"/>
/// is <see cref="ReviewFindings.Open"/>, <see cref="ReviewFindings.Fixed"/> or <see cref="ReviewFindings.Closed"/>.
/// </summary>
public sealed record ReviewFinding(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("line")] int? Line,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("suggestion")] string? Suggestion,
    [property: JsonPropertyName("thread")] int? Thread = null,
    [property: JsonPropertyName("status")] string? Status = null);

/// <summary>Reviews and their conversation (PostgreSQL routines).</summary>
public interface IReviewStore
{
    Task<long> InsertAsync(string repository, int pullRequestId, string title, string author, ProviderKey provider, string threadId, string? spaceId, CancellationToken cancellationToken);

    Task<Review?> GetAsync(long id, CancellationToken cancellationToken);

    Task<Review?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken);

    /// <summary>Reviews posted to their PR (re-checked after pushes until the PR closes).</summary>
    Task<IReadOnlyList<Review>> ListPostedAsync(CancellationToken cancellationToken);

    /// <summary>The PR's latest review, or null.</summary>
    Task<Review?> FindLatestAsync(string repository, int pullRequestId, CancellationToken cancellationToken);

    Task SaveAsync(Review review, CancellationToken cancellationToken);

    Task AddMessageAsync(long reviewId, string direction, string author, string text, CancellationToken cancellationToken);

    Task<IReadOnlyList<IdeaMessage>> ListMessagesAsync(long reviewId, CancellationToken cancellationToken);

    /// <summary>The thread ids of a provider's reviews that aren't closed: the chat poller reads them.</summary>
    Task<IReadOnlyList<string>> ListOpenThreadsAsync(ProviderKey provider, CancellationToken cancellationToken);
}

/// <summary>
/// The agent ends a message with a fenced <c>review-findings</c> block holding <c>{ "summary": …, "findings": [ … ] }</c>
/// when it has (new) findings. This finds it, validates it, and returns the message without it.
/// </summary>
public static partial class ReviewFindings
{
    public const int MaxFindings = 15;

    /// <summary>Only what matters: <c>breaks</c> (a bug, crash, data loss, security hole, breaking change) or <c>performance</c>.</summary>
    public const string Breaks = "breaks";
    public const string Performance = "performance";
    public static readonly string[] Severities = [Breaks, Performance];

    public const string Open = "open";
    public const string Fixed = "fixed";
    public const string Closed = "closed";
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    public static (string Text, ReviewResult? Result, string? Problem) Extract(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var match = Block().Match(reply);
        if (!match.Success)
        {
            return (reply.Trim(), null, null);
        }

        var text = (reply[..match.Index] + reply[(match.Index + match.Length)..]).Trim();
        try
        {
            var result = JsonSerializer.Deserialize<ReviewResult>(match.Groups["json"].Value, s_json);
            var findings = result?.Findings ?? [];
            var problem = result is null || string.IsNullOrWhiteSpace(result.Summary) ? "it needs a summary" : Problem(findings);
            return problem is null
                ? (text, result! with { Findings = Normalize(findings) }, null)
                : (text, null, problem);
        }
        catch (JsonException ex)
        {
            return (text, null, "the review-findings block isn't valid JSON: " + ex.Message);
        }
    }

    /// <summary>Why <paramref name="findings"/> can't be used, or null.</summary>
    public static string? Problem(IReadOnlyList<ReviewFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings.Count > MaxFindings ? $"more than {MaxFindings} findings (keep the important ones)"
            : findings.FirstOrDefault(f => string.IsNullOrWhiteSpace(f.Title)) is not null ? "every finding needs a title"
            : findings.FirstOrDefault(f => !Severities.Contains(f.Severity?.ToLowerInvariant())) is { } bad ? $"\"{bad.Title}\" has severity \"{bad.Severity}\" (use {string.Join(", ", Severities)})"
            : findings.FirstOrDefault(f => f.File is { } path && !IsRelativePath(path)) is { } outside ? $"\"{outside.Title}\": the file must be a path inside the repository"
            : findings.FirstOrDefault(f => f.Line is < 1) is { } line ? $"\"{line.Title}\": the line must be 1 or more"
            : null;
    }

    public static IReadOnlyList<ReviewFinding> Normalize(IReadOnlyList<ReviewFinding> findings) =>
        [.. findings.Select(f => f with { Severity = f.Severity.ToLowerInvariant(), File = f.File?.TrimStart('/') })];

    /// <summary>The <c>review-recheck</c> block of a re-check turn: the statuses of <paramref name="posted"/> findings, and new ones.</summary>
    public static (string Text, Recheck? Recheck, string? Problem) ExtractRecheck(string reply, int posted)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var match = RecheckBlock().Match(reply);
        if (!match.Success)
        {
            return (reply.Trim(), null, null);
        }

        var text = (reply[..match.Index] + reply[(match.Index + match.Length)..]).Trim();
        try
        {
            var recheck = JsonSerializer.Deserialize<Recheck>(match.Groups["json"].Value, s_json);
            var items = recheck?.Findings ?? [];
            var problem = recheck is null ? "it's empty"
                : items.FirstOrDefault(i => i.Number < 1 || i.Number > posted) is { } outside ? $"#{outside.Number} isn't one of the {posted} posted findings"
                : items.FirstOrDefault(i => i.Status?.ToLowerInvariant() is not (Open or Fixed or Closed)) is { } bad ? $"#{bad.Number} has status \"{bad.Status}\" (use open, fixed or closed)"
                : Problem(recheck.New ?? []);
            return problem is null
                ? (text, recheck! with { Findings = [.. items.Select(i => i with { Status = i.Status.ToLowerInvariant() })], New = Normalize(recheck.New ?? []) }, null)
                : (text, null, problem);
        }
        catch (JsonException ex)
        {
            return (text, null, "the review-recheck block isn't valid JSON: " + ex.Message);
        }
    }

    /// <summary>The findings in chat: numbered, worst first in the agent's order, each with where, why and the fix.</summary>
    public static string Render(ReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = new List<string> { $"🔍 **Review:** {result.Summary.Trim()}" };
        if (result.Findings.Count == 0)
        {
            lines.Add("Nothing that breaks the app or slows it down.");
            return string.Join('\n', lines);
        }

        lines.Add(string.Empty);
        for (var i = 0; i < result.Findings.Count; i++)
        {
            var f = result.Findings[i];
            lines.Add($"**{i + 1}.** {Mark(f)} **{f.Title.Trim()}**{Where(f, " · ")}");
            lines.AddRange(Body(f).Select(l => $"   {l}"));
        }

        lines.Add(string.Empty);
        lines.Add(Legend);
        return string.Join('\n', lines);
    }

    /// <summary>A finding's own PR thread, at its line: the title, why, and the fix. Short on purpose.</summary>
    public static string ThreadText(ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return string.Join('\n', [$"{Icon(finding.Severity)} **{finding.Title.Trim()}**", .. Body(finding)]);
    }

    /// <summary>
    /// The PR's main review message: every posted finding with its status (✅ once fixed), edited in place after each
    /// re-check. <paramref name="checkedAt"/> is the commit the statuses are about.
    /// </summary>
    public static string MainMessage(ReviewResult result, string? checkedAt)
    {
        ArgumentNullException.ThrowIfNull(result);
        var open = result.Findings.Count(f => (f.Status ?? Open) == Open);
        var fixedCount = result.Findings.Count(f => f.Status == Fixed);
        var lines = new List<string>
        {
            $"**Review** · {result.Findings.Count} finding(s): {open} open, {fixedCount} fixed{(checkedAt is { Length: > 0 } c ? $" · checked at {c[..Math.Min(7, c.Length)]}" : string.Empty)}",
            string.Empty,
            result.Summary.Trim(),
        };
        if (result.Findings.Count > 0)
        {
            lines.AddRange([string.Empty, "| | Finding | Where |", "|---|---|---|"]);
            lines.AddRange(result.Findings.Select(f => $"| {Mark(f)} | {Cell(f.Title)} | {Where(f, string.Empty)} |"));
        }

        lines.AddRange([string.Empty, Legend, "Reply on a finding's thread if you disagree. agentd checks again after every push."]);
        return string.Join('\n', lines);
    }

    public const string Legend = "🔴 can break the app · 🟠 performance · ✅ fixed · ⚪ closed";

    /// <summary>The severity's icon (older reviews' blocker/major/minor/nit included).</summary>
    public static string Icon(string severity) => severity switch
    {
        Performance or "minor" or "nit" => "🟠",
        _ => "🔴",
    };

    /// <summary>The status icon once there is one, otherwise the severity's.</summary>
    public static string Mark(ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.Status switch
        {
            Fixed => "✅",
            Closed => "⚪",
            _ => Icon(finding.Severity),
        };
    }

    private static IEnumerable<string> Body(ReviewFinding f)
    {
        if (!string.IsNullOrWhiteSpace(f.Detail))
        {
            yield return f.Detail.ReplaceLineEndings(" ").Trim();
        }

        if (!string.IsNullOrWhiteSpace(f.Suggestion))
        {
            yield return $"**Fix:** {f.Suggestion.ReplaceLineEndings(" ").Trim()}";
        }
    }

    private static string Where(ReviewFinding f, string prefix) =>
        f.File is null ? string.Empty : $"{prefix}`{f.File}{(f.Line is { } l ? $":{l}" : string.Empty)}`";

    private static string Cell(string text) => text.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal).Trim();

    private static bool IsRelativePath(string path) =>
        path.Length is > 0 and <= 400 && !path.Contains("..", StringComparison.Ordinal) && !path.Contains('\\', StringComparison.Ordinal)
        && !Path.IsPathRooted(path.TrimStart('/')) && !path.Contains(':', StringComparison.Ordinal);

    [GeneratedRegex(@"```review-findings\s*\n(?<json>[\s\S]*?)\n```", RegexOptions.IgnoreCase)]
    private static partial Regex Block();

    [GeneratedRegex(@"```review-recheck\s*\n(?<json>[\s\S]*?)\n```", RegexOptions.IgnoreCase)]
    private static partial Regex RecheckBlock();
}
