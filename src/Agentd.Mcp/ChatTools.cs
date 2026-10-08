using System.ComponentModel;
using System.Globalization;
using System.Text;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Agentd.Mcp;

/// <summary>
/// <c>!chat</c>'s read-only Azure DevOps tools. agentd answers them with its own credentials, so the agent never sees a
/// token; only a chat's token (<see cref="McpHosting.ChatPolicy"/>) lists or calls them.
/// </summary>
[McpServerToolType]
[Authorize(Policy = McpHosting.ChatPolicy)]
public sealed class ChatTools(IAzureDevOpsSearch search, IWorkItemSource workItems, IPullRequestService pullRequests, IRepositoryRegistry repositories)
{
    /// <summary>What a tool answers at most; longer answers are cut with a note.</summary>
    public const int MaxAnswer = 12_000;

    [McpServerTool(Name = "ado_search_work_items"), Description("Search Azure DevOps work items in the project (newest change first). Every filter is optional.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Tool argument names, snake_case like the other agentd tools.")]
    public async Task<string> SearchWorkItems(
        [Description("Words in the title.")] string? text = null,
        [Description("User Story, Bug, Task, Feature, …")] string? type = null,
        [Description("Active, New, Closed, …")] string? state = null,
        [Description("A person's display name or email.")] string? assigned_to = null,
        [Description("One tag.")] string? tag = null,
        [Description("Only under this area path.")] string? area_path = null,
        [Description("Only under this iteration path, or @CurrentIteration for the current sprint.")] string? iteration = null,
        [Description("How many (1–50, default 20).")] int top = 20,
        CancellationToken cancellationToken = default)
    {
        var hits = await Ado(() => search.SearchWorkItemsAsync(new WorkItemQuery(text, type, state, assigned_to, tag, area_path, iteration, top), cancellationToken)).ConfigureAwait(false);
        return hits.Count == 0
            ? "No work items match."
            : Cap(string.Join('\n', hits.Select(h => string.Create(CultureInfo.InvariantCulture,
                $"#{h.Id} [{h.Type}] {h.State} · {h.Title}{(h.AssignedTo is null ? string.Empty : $" · {h.AssignedTo}")}{(h.Tags.Count == 0 ? string.Empty : $" · tags: {string.Join(", ", h.Tags)}")}{(h.ChangedAt is { } at ? $" · changed {at:yyyy-MM-dd}" : string.Empty)}"))));
    }

    [McpServerTool(Name = "ado_get_work_item"), Description("One work item: title, state, area, tags, description, acceptance criteria, repro steps and comments.")]
    public async Task<string> GetWorkItem([Description("The work item id.")] int id, CancellationToken cancellationToken = default)
    {
        var item = await Ado(() => workItems.GetAsync(id, cancellationToken)).ConfigureAwait(false);
        if (item is null)
        {
            return $"Work item #{id} doesn't exist (or isn't visible).";
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"#{item.Id} {item.Title}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"State: {item.State} · Area: {item.AreaPath}{(item.Tags.Count == 0 ? string.Empty : $" · Tags: {string.Join(", ", item.Tags)}")}");
        Section(sb, "Description", item.Description);
        Section(sb, "Acceptance criteria", item.AcceptanceCriteria);
        Section(sb, "Repro steps", item.ReproSteps);
        if (item.Comments.Count > 0)
        {
            sb.AppendLine().AppendLine("Comments:");
            foreach (var c in item.Comments)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {c.Author} ({c.CreatedAt:yyyy-MM-dd}): {c.Text.Trim()}");
            }
        }

        return Cap(sb.ToString());
    }

    [McpServerTool(Name = "ado_list_pull_requests"), Description("Pull requests in the project, or in one repository, newest first.")]
    public async Task<string> ListPullRequests(
        [Description("A repository name; empty for the whole project.")] string? repository = null,
        [Description("active (default), completed, abandoned or all.")] string status = "active",
        [Description("How many (1–50, default 20).")] int top = 20,
        CancellationToken cancellationToken = default)
    {
        var hits = await Ado(() => search.ListPullRequestsAsync(repository, status, top, cancellationToken)).ConfigureAwait(false);
        return hits.Count == 0
            ? "No pull requests match."
            : Cap(string.Join('\n', hits.Select(p => string.Create(CultureInfo.InvariantCulture,
                $"!{p.Id} [{p.Repository}] {p.Status} · {p.Title} · by {p.Author} · {p.SourceBranch} → {p.TargetBranch} · {p.CreatedAt:yyyy-MM-dd}"))));
    }

    [McpServerTool(Name = "ado_get_pull_request"), Description("One pull request of a registered repository: description, branches, linked work items and its comment threads.")]
    public async Task<string> GetPullRequest(
        [Description("The repository's name (registered in agentd).")] string repository,
        [Description("The pull request id.")] int id,
        CancellationToken cancellationToken = default)
    {
        var repo = RepositoryName.Create(repository ?? string.Empty) is { IsSuccess: true } name ? await repositories.GetAsync(name.Value, cancellationToken).ConfigureAwait(false) : null;
        if (repo is null)
        {
            var known = await repositories.ListAsync(cancellationToken).ConfigureAwait(false);
            return $"`{repository}` isn't a registered repository. Registered: {string.Join(", ", known.Select(r => r.Name.Value))}.";
        }

        var pr = await Ado(() => pullRequests.GetAsync(repo, id, cancellationToken)).ConfigureAwait(false);
        if (pr is null)
        {
            return $"PR !{id} doesn't exist in {repo.Name}.";
        }

        var comments = await Ado(() => pullRequests.ListCommentsAsync(repo, id, cancellationToken)).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"!{pr.Id} {pr.Title} ({pr.Status}{(pr.IsDraft ? ", draft" : string.Empty)})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"By {pr.Author} · {pr.SourceBranch} → {pr.TargetBranch} · head {pr.SourceCommit} · {pr.Url}");
        if (pr.WorkItems is { Count: > 0 } linked)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Work items: {string.Join(", ", linked.Select(w => $"#{w}"))}");
        }

        Section(sb, "Description", pr.Description);
        if (comments.Count > 0)
        {
            sb.AppendLine().AppendLine("Comments (people's, by thread):");
            foreach (var c in comments)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- thread {c.ThreadId} [{c.ThreadStatus}]{(c.FilePath is null ? string.Empty : $" {c.FilePath}{(c.Line is { } l ? $":{l}" : string.Empty)}")} {c.Author}: {c.Content.ReplaceLineEndings(" ").Trim()}");
            }
        }

        return Cap(sb.ToString());
    }

    private static void Section(StringBuilder sb, string title, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"{title}:").AppendLine(text.Trim());
        }
    }

    private static string Cap(string text) => text.Length <= MaxAnswer ? text : text[..MaxAnswer] + "\n… (cut: narrow the search for the rest)";

    /// <summary>An Azure DevOps failure becomes a tool error the agent can read (and tell the developer), never a crash.</summary>
#pragma warning disable CA1031 // any failure is reported to the agent
    private static async Task<T> Ado<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or McpException))
        {
            throw new McpException($"Azure DevOps didn't answer: {ex.Message}");
        }
#pragma warning restore CA1031
    }
}
