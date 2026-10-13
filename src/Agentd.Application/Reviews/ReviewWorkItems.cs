using System.Globalization;
using System.Text.RegularExpressions;
using Agentd.Application.Ports;

namespace Agentd.Application.Reviews;

/// <summary>
/// The work items behind a change, for a reviewer (docs/architect/review-sessions.md §3): what it's supposed to do. The
/// linked ones (a PR's links, or the id in an <c>ai/&lt;id&gt;-…</c> branch), and a task's parent user story, whose
/// acceptance criteria usually say what "done" means.
/// </summary>
public sealed partial class ReviewWorkItems(IWorkItemSource? workItems = null)
{
    /// <summary>Linked work items read for one review (the agent can read more itself).</summary>
    public const int MaxLinked = 3;

    /// <summary>The work item a branch names: <c>ai/5617-deploy</c>, <c>feature/5617-x</c>, <c>5617_fix</c>; none otherwise.</summary>
    public static IReadOnlyList<int> FromBranch(string? branch) =>
        branch is not null && BranchId().Match(branch) is { Success: true } m && int.TryParse(m.Groups["id"].Value, CultureInfo.InvariantCulture, out var id)
            ? [id]
            : [];

    /// <summary>The reviewer's "Linked work items" section, each with its acceptance criteria and description, a task's parent after it.</summary>
    public async Task<string> ContextAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (workItems is null || ids.Count == 0)
        {
            return "\n\nLinked work items: none (judge the change by its title, description and commits).";
        }

        var linked = ids.Distinct().Take(MaxLinked).ToList();
        var shown = linked.ToHashSet();   // a parent that's linked itself is shown once, as linked
        var parts = new List<string>();
        foreach (var id in linked)
        {
            if (await ReadAsync(id, parts, ct).ConfigureAwait(false) is { ParentId: { } parent } item && IsTask(item) && shown.Add(parent))
            {
                await ReadAsync(parent, parts, ct, $"Parent of WI-{id}: ").ConfigureAwait(false);
            }
        }

        return "\n\nLinked work items:\n" + string.Join("\n\n", parts);
    }

    private async Task<WorkItemDetails?> ReadAsync(int id, List<string> parts, CancellationToken ct, string prefix = "")
    {
        try
        {
            if (await workItems!.GetAsync(id, ct).ConfigureAwait(false) is not { } item)
            {
                parts.Add($"{prefix}WI-{id}: not found.");
                return null;
            }

            parts.Add($"{prefix}WI-{item.Id}{(item.Type is { Length: > 0 } type ? $" ({type})" : string.Empty)} \"{item.Title}\" ({item.State})"
                + (string.IsNullOrWhiteSpace(item.AcceptanceCriteria) ? "\nAcceptance criteria: none written." : $"\nAcceptance criteria:\n{Clip(item.AcceptanceCriteria, 2000)}")
                + (string.IsNullOrWhiteSpace(item.Description) ? string.Empty : $"\nDescription:\n{Clip(item.Description, 1500)}"));
            return item;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            parts.Add($"{prefix}WI-{id}: couldn't be read ({ex.Message}).");
            return null;
        }
    }

    private static bool IsTask(WorkItemDetails item) => string.Equals(item.Type, "Task", StringComparison.OrdinalIgnoreCase);

    private static string Clip(string text, int max)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..(max - 1)] + "…";
    }

    [GeneratedRegex(@"^(?:[^/]+/)?(?<id>\d{1,9})(?:[-_]|$)")]
    private static partial Regex BranchId();
}
