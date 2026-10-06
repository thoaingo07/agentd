using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agentd.Domain.Messaging;

namespace Agentd.Application.Ideas;

/// <summary>Brainstorming → Proposed → Created | Discarded → Closed.</summary>
public static class IdeaStatus
{
    public const string Brainstorming = "Brainstorming";
    public const string Proposed = "Proposed";
    public const string Created = "Created";
    public const string Discarded = "Discarded";
    public const string Closed = "Closed";
}

/// <summary>An idea being brainstormed in a chat thread, before any work item exists.</summary>
public sealed record Idea(
    long Id,
    string Repository,
    string Title,
    string Author,
    ProviderKey Provider,
    string ThreadId,
    string? SpaceId,
    string Status,
    Guid? Session,
    string? Model,
    string? Effort,
    string? Worktree,
    IReadOnlyList<WorkItemDraft>? Drafts,
    IReadOnlyList<int> CreatedWorkItems);

/// <summary>One proposed work item. Tasks may name their parent story by its index in the list.</summary>
public sealed record WorkItemDraft(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("acceptanceCriteria")] string? AcceptanceCriteria,
    [property: JsonPropertyName("estimate")] double? Estimate,
    [property: JsonPropertyName("parent")] int? Parent,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags);

public sealed record IdeaMessage(string Direction, string Author, string Text, DateTimeOffset At);

/// <summary>An idea as the Web UI lists it: counts instead of the drafts and the conversation.</summary>
public sealed record IdeaSummary(
    long Id, string Repository, string Title, string Author, string Status, string? Model, string? Effort,
    int Drafts, IReadOnlyList<int> CreatedWorkItems, int Messages, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Ideas and their conversation (PostgreSQL routines).</summary>
public interface IIdeaStore
{
    Task<long> InsertAsync(string repository, string title, string author, ProviderKey provider, string threadId, string? spaceId, CancellationToken cancellationToken);

    Task<Idea?> GetAsync(long id, CancellationToken cancellationToken);

    Task<Idea?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken);

    Task SaveAsync(Idea idea, CancellationToken cancellationToken);

    Task AddMessageAsync(long ideaId, string direction, string author, string text, CancellationToken cancellationToken);

    Task<IReadOnlyList<IdeaMessage>> ListMessagesAsync(long ideaId, CancellationToken cancellationToken);

    /// <summary>Newest first: one idea (<paramref name="id"/>), those that created <paramref name="workItem"/>, or all.</summary>
    Task<IReadOnlyList<IdeaSummary>> ListSummariesAsync(long? id, int? workItem, int limit, CancellationToken cancellationToken);

    /// <summary>The thread ids of a provider's ideas that aren't closed: the chat poller reads them like job threads.</summary>
    Task<IReadOnlyList<string>> ListOpenThreadsAsync(ProviderKey provider, CancellationToken cancellationToken);
}

/// <summary>
/// One read-only agent turn in a chat thread's checkout, on its model and effort: an idea's brainstorm, or a PR review
/// (<paramref name="Kind"/>; <paramref name="IdeaId"/> is then the review's id).
/// </summary>
public sealed record BrainstormTurn(
    long IdeaId, string Worktree, Guid Session, bool Resume, string Prompt, string? Model = null, string? Effort = null, ThreadTurnKind Kind = ThreadTurnKind.Brainstorm);

/// <summary>What a read-only thread turn is for: it picks the agent's instructions and where its transcript goes.</summary>
public enum ThreadTurnKind
{
    Brainstorm,
    Review,

    /// <summary>A finished job's session, resumed read-only to answer questions after its PR is merged (talk only).</summary>
    FollowUp,
}

/// <summary>The model and effort people may pick for an idea (passed to the CLI's <c>--model</c> / <c>--effort</c>).</summary>
public static partial class BrainstormSettings
{
    public static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>An alias (fable, opus, sonnet, haiku) or a full model name: letters, digits, dots and dashes.</summary>
    public static bool IsModel(string? value) => value is { Length: > 0 and <= 64 } && ModelPattern().IsMatch(value);

    public static bool IsEffort(string? value) => value is not null && Efforts.Contains(value.ToLowerInvariant());

    /// <summary>Takes <c>--model x</c> and <c>--effort y</c> (and <c>--repo z</c>) off the front or anywhere in the words.</summary>
    public static (string Text, string? Repo, string? Model, string? Effort, string? Problem) Parse(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        string? repo = null, model = null, effort = null, problem = null;
        var rest = new List<string>();
        for (var i = 0; i < words.Count; i++)
        {
            var flag = words[i].ToLowerInvariant();
            if (flag is "--repo" or "--model" or "--effort" && i + 1 < words.Count)
            {
                var value = words[++i];
                switch (flag)
                {
                    case "--repo": repo = value; break;
                    case "--model": model = value; problem ??= IsModel(value) ? null : $"`{value}` isn't a model name (use fable, opus, sonnet or a full model name)."; break;
                    default: effort = value.ToLowerInvariant(); problem ??= IsEffort(value) ? null : $"`{value}` isn't an effort level ({string.Join(", ", Efforts)})."; break;
                }
            }
            else
            {
                rest.Add(words[i]);
            }
        }

        return (string.Join(' ', rest), repo, model, effort, problem);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]*$")]
    private static partial Regex ModelPattern();
}

/// <summary>The agent's reply (its final message), or why there is none.</summary>
public sealed record BrainstormReply(string? Text, DateTimeOffset? UsageLimitedUntil, string? Error);

/// <summary>Runs brainstorm turns: read-only tools, no agentd tools, no edits (implemented with Claude Code).</summary>
public interface IBrainstormAgent
{
    Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken);
}

/// <summary>
/// The agent proposes work items by ending its message with a fenced <c>work-items</c> block holding a JSON
/// array. This finds it, parses it, and returns the message without it.
/// </summary>
public static partial class WorkItemDrafts
{
    public const int MaxItems = 10;
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    public static (string Text, IReadOnlyList<WorkItemDraft>? Drafts, string? Problem) Extract(string reply)
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
            var drafts = JsonSerializer.Deserialize<List<WorkItemDraft>>(match.Groups["json"].Value, s_json) ?? [];
            var problem = drafts.Count == 0 ? "the list is empty"
                : drafts.Count > MaxItems ? $"more than {MaxItems} items"
                : drafts.FirstOrDefault(d => string.IsNullOrWhiteSpace(d.Title) || d.Type is not ("User Story" or "Task")) is { } bad ? $"\"{bad.Title}\" needs a title and type User Story or Task"
                : drafts.Select((d, i) => (d, i)).FirstOrDefault(x => x.d.Parent is { } p && (p < 0 || p >= drafts.Count || p == x.i || drafts[p].Type != "User Story")) is { d: not null } orphan ? $"\"{orphan.d.Title}\" has a parent that isn't a story in the list"
                : null;
            return problem is null ? (text, drafts, null) : (text, null, problem);
        }
        catch (JsonException ex)
        {
            return (text, null, "the work-items block isn't valid JSON: " + ex.Message);
        }
    }

    /// <summary>The drafts for people: stories with their tasks underneath.</summary>
    public static string Render(IReadOnlyList<WorkItemDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var lines = new List<string> { "🗂 **Proposed work items**" };
        void Add(WorkItemDraft d, int i, string indent)
        {
            var est = d.Estimate is { } e ? $" · {e:0.#} {(d.Type == "Task" ? "h" : "pts")}" : string.Empty;
            lines.Add($"{indent}{i + 1}. **{d.Type}: {d.Title}**{est}");
            if (!string.IsNullOrWhiteSpace(d.AcceptanceCriteria))
            {
                lines.Add($"{indent}   ✓ {d.AcceptanceCriteria.ReplaceLineEndings(" ").Trim()}");
            }
        }

        for (var i = 0; i < drafts.Count; i++)
        {
            if (drafts[i].Parent is not null)
            {
                continue;
            }

            Add(drafts[i], i, string.Empty);
            for (var j = 0; j < drafts.Count; j++)
            {
                if (drafts[j].Parent == i)
                {
                    Add(drafts[j], j, "      ");
                }
            }
        }

        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"```work-items\s*\n(?<json>[\s\S]*?)\n```", RegexOptions.IgnoreCase)]
    private static partial Regex Block();
}
