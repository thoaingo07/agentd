using Agentd.Application.Ideas;
using Agentd.Application.Queries;

namespace Agentd.Bff.ViewModels;

public sealed record IdeaSummaryVm(
    long Id, string Repo, string Title, string Author, string Status, string? Model, string? Effort,
    int Drafts, IReadOnlyList<int> CreatedWorkItems, int Messages, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static IdeaSummaryVm From(IdeaSummary s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Id, s.Repository, s.Title, s.Author, s.Status, s.Model, s.Effort, s.Drafts, s.CreatedWorkItems, s.Messages, s.CreatedAt, s.UpdatedAt);
    }
}

/// <summary>A proposed work item; a task's <c>parent</c> is its story's index in the list.</summary>
public sealed record WorkItemDraftVm(string Type, string Title, string? Description, string? AcceptanceCriteria, double? Estimate, int? Parent, IReadOnlyList<string> Tags);

/// <summary>One message of the idea's conversation: <c>in</c> from a person, <c>out</c> from the agent (Markdown).</summary>
public sealed record IdeaMessageVm(string Direction, string Author, string Text, DateTimeOffset At);

public sealed record IdeaDetailVm(IdeaSummaryVm Idea, IReadOnlyList<WorkItemDraftVm> Drafts, IReadOnlyList<IdeaMessageVm> Messages)
{
    public static IdeaDetailVm From(IdeaDetail d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new(
            IdeaSummaryVm.From(d.Summary),
            d.Drafts.Select(w => new WorkItemDraftVm(w.Type, w.Title, w.Description, w.AcceptanceCriteria, w.Estimate, w.Parent, w.Tags ?? [])).ToList(),
            d.Messages.Select(m => new IdeaMessageVm(m.Direction, m.Author, m.Text, m.At)).ToList());
    }
}
