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
    Uri? Url);

/// <summary>Azure DevOps work items (implemented in Infrastructure.AzureDevOps).</summary>
public interface IWorkItemSource
{
    Task<IReadOnlyList<WorkItemRef>> QueryTaggedAsync(string tag, string excludeTag, IReadOnlyList<string> states, CancellationToken cancellationToken);

    Task<WorkItemDetails?> GetAsync(int id, CancellationToken cancellationToken);

    /// <summary>Adds the claim tag guarded by the revision; false if someone else changed the item first.</summary>
    Task<bool> TryClaimAsync(int id, int rev, string claimTag, CancellationToken cancellationToken);

    Task AddCommentAsync(int id, string text, CancellationToken cancellationToken);
}
