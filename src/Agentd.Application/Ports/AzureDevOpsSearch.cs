namespace Agentd.Application.Ports;

/// <summary>A work item search for <c>!chat</c>. Every field is optional; the values are matched, never pasted into WIQL.</summary>
/// <param name="Text">Words in the title.</param>
/// <param name="Type">User Story, Bug, Task, …</param>
/// <param name="State">Active, New, Closed, …</param>
/// <param name="AssignedTo">A display name or email.</param>
/// <param name="Tag">One tag.</param>
/// <param name="AreaPath">Under this area path.</param>
/// <param name="Iteration">Under this iteration path, or <c>@CurrentIteration</c>.</param>
/// <param name="Top">At most this many (1–50).</param>
public sealed record WorkItemQuery(string? Text, string? Type, string? State, string? AssignedTo, string? Tag, string? AreaPath, string? Iteration, int Top = 20);

public sealed record WorkItemHit(int Id, string Type, string Title, string State, string? AssignedTo, IReadOnlyList<string> Tags, DateTimeOffset? ChangedAt);

public sealed record PullRequestHit(int Id, string Repository, string Title, string Author, string Status, string SourceBranch, string TargetBranch, DateTimeOffset CreatedAt);

/// <summary>A pipeline (build definition).</summary>
public sealed record PipelineHit(int Id, string Name, string Folder);

/// <summary>A run of a pipeline. <paramref name="Result"/>: succeeded, failed, canceled, partiallySucceeded, or null while running.</summary>
public sealed record BuildHit(int Id, string Pipeline, string Number, string Status, string? Result, string Branch, string? RequestedFor, string Reason, string? Commit,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

/// <summary>A failed (or warning) step of a run: its errors and the end of its log.</summary>
public sealed record BuildFailure(string Step, string Kind, string Result, IReadOnlyList<string> Issues, string? LogTail);

public sealed record BuildDetail(BuildHit Build, IReadOnlyList<BuildFailure> Failures);

/// <summary>A wiki search hit: the page and the matched snippets (search highlights, without markup).</summary>
public sealed record WikiHit(string Wiki, string Path, IReadOnlyList<string> Snippets);

/// <summary>A wiki page's markdown and its direct sub-pages; <paramref name="Wikis"/>: every wiki in the project.</summary>
public sealed record WikiPage(string Wiki, string Path, string? Content, IReadOnlyList<string> SubPages, IReadOnlyList<string> Wikis);

/// <summary>Read-only searches across the Azure DevOps project, for <c>!chat</c>'s tools (implemented in Infrastructure.AzureDevOps).</summary>
public interface IAzureDevOpsSearch
{
    Task<IReadOnlyList<WorkItemHit>> SearchWorkItemsAsync(WorkItemQuery query, CancellationToken cancellationToken);

    /// <summary>PRs in the project (or one repository), newest first. <paramref name="status"/>: active, completed, abandoned or all.</summary>
    Task<IReadOnlyList<PullRequestHit>> ListPullRequestsAsync(string? repository, string status, int top, CancellationToken cancellationToken);

    /// <summary>The project's pipelines whose name contains <paramref name="name"/> (all when empty).</summary>
    Task<IReadOnlyList<PipelineHit>> ListPipelinesAsync(string? name, CancellationToken cancellationToken);

    /// <summary>Runs, newest first: of one pipeline (by name or id) and/or branch; <paramref name="result"/>: succeeded, failed, canceled or any.</summary>
    Task<IReadOnlyList<BuildHit>> ListBuildsAsync(string? pipeline, string? branch, string? result, int top, CancellationToken cancellationToken);

    /// <summary>One run, with each failed step's errors and the last lines of its log; null when it doesn't exist.</summary>
    Task<BuildDetail?> GetBuildAsync(int id, CancellationToken cancellationToken);

    /// <summary>Full-text search of the project's wikis (Azure DevOps Search), best match first.</summary>
    Task<IReadOnlyList<WikiHit>> SearchWikiAsync(string text, int top, CancellationToken cancellationToken);

    /// <summary>A page by path (the wiki's root when empty) of <paramref name="wiki"/> (the project wiki when empty); null when there's no such page.</summary>
    Task<WikiPage?> GetWikiPageAsync(string? wiki, string? path, CancellationToken cancellationToken);
}
