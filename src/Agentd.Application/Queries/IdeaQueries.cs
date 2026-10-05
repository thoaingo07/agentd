using Agentd.Application.Abstractions;
using Agentd.Application.Ideas;

namespace Agentd.Application.Queries;

/// <summary>The Ideas page: the newest ideas, or those that created <see cref="WorkItem"/> ("born from idea #N").</summary>
public sealed record GetIdeas(int? WorkItem = null);

public sealed record GetIdea(long Id);

/// <summary>An idea's page: its summary, the latest drafts and the whole conversation (it outlives the chat thread).</summary>
public sealed record IdeaDetail(IdeaSummary Summary, IReadOnlyList<WorkItemDraft> Drafts, IReadOnlyList<IdeaMessage> Messages);

public sealed class GetIdeasHandler(IIdeaStore ideas) : IQueryHandler<GetIdeas, IReadOnlyList<IdeaSummary>>
{
    /// <summary>The list shows this many; older ideas stay in the database.</summary>
    public const int Limit = 200;

    public Task<IReadOnlyList<IdeaSummary>> Handle(GetIdeas query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ideas.ListSummariesAsync(null, query.WorkItem, Limit, cancellationToken);
    }
}

public sealed class GetIdeaHandler(IIdeaStore ideas) : IQueryHandler<GetIdea, IdeaDetail?>
{
    public async Task<IdeaDetail?> Handle(GetIdea query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var summary = (await ideas.ListSummariesAsync(query.Id, null, 1, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (summary is null || await ideas.GetAsync(query.Id, cancellationToken).ConfigureAwait(false) is not { } idea)
        {
            return null;
        }

        return new IdeaDetail(summary, idea.Drafts ?? [], await ideas.ListMessagesAsync(query.Id, cancellationToken).ConfigureAwait(false));
    }
}
