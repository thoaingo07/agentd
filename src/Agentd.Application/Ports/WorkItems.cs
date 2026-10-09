namespace Agentd.Application.Ports;

public sealed record WorkItemRef(int Id, int Rev);

public sealed record WorkItemComment(string Author, DateTimeOffset CreatedAt, string Text);

public sealed record WorkItemDetails(
    int Id,
    int Rev,
    string Title,
    string State,
    string AreaPath,
    IReadOnlyList<string> Tags,
    string? Description,
    string? AcceptanceCriteria,
    string? ReproSteps,
    IReadOnlyList<WorkItemComment> Comments,
    Uri? Url,
    Guid? AssignedToId = null,
    string? AssignedTo = null);

/// <summary>Azure DevOps work items (implemented in Infrastructure.AzureDevOps).</summary>
public interface IWorkItemSource
{
    Task<IReadOnlyList<WorkItemRef>> QueryTaggedAsync(string tag, string excludeTag, IReadOnlyList<string> states, CancellationToken cancellationToken);

    Task<WorkItemDetails?> GetAsync(int id, CancellationToken cancellationToken);

    /// <summary>Adds the claim tag guarded by the revision; false if someone else changed the item first.</summary>
    Task<bool> TryClaimAsync(int id, int rev, string claimTag, CancellationToken cancellationToken);

    Task AddCommentAsync(int id, string text, CancellationToken cancellationToken);

    /// <summary>Creates a work item (the only write besides tags and comments): returns its id and web link.</summary>
    Task<CreatedWorkItem> CreateAsync(NewWorkItem item, CancellationToken cancellationToken);
}

/// <summary>A work item to create. <paramref name="Estimate"/> is story points for stories, hours for tasks.</summary>
/// <param name="Type">"User Story" or "Task".</param>
/// <param name="Title">The title.</param>
/// <param name="Description">Markdown-ish text (stored as simple HTML).</param>
/// <param name="AcceptanceCriteria">Markdown-ish text.</param>
/// <param name="Tags">Tags.</param>
/// <param name="Estimate">Story points (stories) or hours (tasks).</param>
/// <param name="ParentId">The parent work item (a task's story).</param>
/// <param name="AreaPath">The area path; null = the project default.</param>
public sealed record NewWorkItem(string Type, string Title, string? Description, string? AcceptanceCriteria, IReadOnlyList<string> Tags, double? Estimate, int? ParentId, string? AreaPath);

public sealed record CreatedWorkItem(int Id, Uri? Url);
