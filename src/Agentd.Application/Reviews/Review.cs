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

/// <summary>The agent's latest findings and its summary of the PR.</summary>
public sealed record ReviewResult(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("findings")] IReadOnlyList<ReviewFinding> Findings);

/// <summary>One finding: where (<paramref name="File"/> relative to the repo root, optional line on the PR's side), how bad, and what to do.</summary>
public sealed record ReviewFinding(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("line")] int? Line,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("suggestion")] string? Suggestion);

/// <summary>Reviews and their conversation (PostgreSQL routines).</summary>
public interface IReviewStore
{
    Task<long> InsertAsync(string repository, int pullRequestId, string title, string author, ProviderKey provider, string threadId, string? spaceId, CancellationToken cancellationToken);

    Task<Review?> GetAsync(long id, CancellationToken cancellationToken);

    Task<Review?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken);

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
    public const int MaxFindings = 30;
    public static readonly string[] Severities = ["blocker", "major", "minor", "nit"];
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
            var problem = result is null || string.IsNullOrWhiteSpace(result.Summary) ? "it needs a summary"
                : findings.Count > MaxFindings ? $"more than {MaxFindings} findings (keep the important ones)"
                : findings.FirstOrDefault(f => string.IsNullOrWhiteSpace(f.Title)) is not null ? "every finding needs a title"
                : findings.FirstOrDefault(f => !Severities.Contains(f.Severity?.ToLowerInvariant())) is { } bad ? $"\"{bad.Title}\" has severity \"{bad.Severity}\" (use {string.Join(", ", Severities)})"
                : findings.FirstOrDefault(f => f.File is { } path && !IsRelativePath(path)) is { } outside ? $"\"{outside.Title}\": the file must be a path inside the repository"
                : findings.FirstOrDefault(f => f.Line is < 1) is { } line ? $"\"{line.Title}\": the line must be 1 or more"
                : null;
            return problem is null
                ? (text, result! with { Findings = [.. findings.Select(f => f with { Severity = f.Severity.ToLowerInvariant(), File = f.File?.TrimStart('/') })] }, null)
                : (text, null, problem);
        }
        catch (JsonException ex)
        {
            return (text, null, "the review-findings block isn't valid JSON: " + ex.Message);
        }
    }

    /// <summary>The findings for people: numbered, worst first in the agent's order, with where and what to do.</summary>
    public static string Render(ReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = new List<string> { $"🔍 **Review summary:** {result.Summary.Trim()}" };
        if (result.Findings.Count == 0)
        {
            lines.Add("No findings: nothing to change.");
            return string.Join('\n', lines);
        }

        lines.Add(string.Empty);
        for (var i = 0; i < result.Findings.Count; i++)
        {
            var f = result.Findings[i];
            var where = f.File is null ? string.Empty : $" · `{f.File}{(f.Line is { } l ? $":{l}" : string.Empty)}`";
            lines.Add($"**{i + 1}.** {Icon(f.Severity)} **{f.Title.Trim()}**{where}");
            if (!string.IsNullOrWhiteSpace(f.Detail))
            {
                lines.Add($"   {f.Detail.ReplaceLineEndings(" ").Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(f.Suggestion))
            {
                lines.Add($"   💡 {f.Suggestion.ReplaceLineEndings(" ").Trim()}");
            }
        }

        return string.Join('\n', lines);
    }

    public static string Icon(string severity) => severity switch
    {
        "blocker" => "🔴 blocker",
        "major" => "🟠 major",
        "minor" => "🟡 minor",
        _ => "⚪ nit",
    };

    private static bool IsRelativePath(string path) =>
        path.Length is > 0 and <= 400 && !path.Contains("..", StringComparison.Ordinal) && !path.Contains('\\', StringComparison.Ordinal)
        && !Path.IsPathRooted(path.TrimStart('/')) && !path.Contains(':', StringComparison.Ordinal);

    [GeneratedRegex(@"```review-findings\s*\n(?<json>[\s\S]*?)\n```", RegexOptions.IgnoreCase)]
    private static partial Regex Block();
}
