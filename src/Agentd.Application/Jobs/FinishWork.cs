using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

/// <summary>The agent called <c>finish</c>: record the PR draft and publish the pull request.</summary>
public sealed record FinishWork(JobId JobId, string Title, string Description, string Summary);

public sealed class FinishWorkHandler(IJobRepository jobs, ICommandHandler<PublishPullRequest, PullRequestRef> publish)
    : ICommandHandler<FinishWork, PullRequestRef>
{
    public async Task<Result<PullRequestRef>> Handle(FinishWork command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var draft = PullRequestDraft.Create(command.Title, command.Description, command.Summary);
        if (!draft.IsSuccess)
        {
            return draft.Error;
        }

        var job = await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return DomainError.NotFound($"Job {command.JobId}");
        }

        // The developer must be heard first: hand over their unread messages and refuse to finish yet.
        if (job.PendingMessages.Count > 0)
        {
            var unread = job.TakePendingMessages();
            var taken = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            return taken.IsSuccess
                ? new DomainError("unread_messages",
                    "Not finished yet: the developer sent messages you haven't seen. Address them, then call finish again." + TakeDeveloperMessagesHandler.Format(unread))
                : taken.Error;
        }

        var finished = job.Finish(draft.Value);
        if (!finished.IsSuccess)
        {
            return finished.Error;
        }

        var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            return saved.Error;
        }

        return await publish.Handle(new PublishPullRequest(job.Id, draft.Value), cancellationToken).ConfigureAwait(false);
    }
}
