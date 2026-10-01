using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;

namespace Agentd.Application.Jobs;

/// <summary>Retry publishing jobs whose previous push/PR attempt failed and whose back-off has passed.</summary>
public sealed record RetryDuePublishes;

public sealed class RetryDuePublishesHandler(
    IJobRepository jobs,
    IClock clock,
    ICommandHandler<PublishPullRequest, PullRequestRef> publish) : ICommandHandler<RetryDuePublishes, int>
{
    public async Task<Result<int>> Handle(RetryDuePublishes command, CancellationToken cancellationToken)
    {
        var publishing = await jobs.ListByStateAsync([JobState.Publishing], cancellationToken).ConfigureAwait(false);
        var retried = 0;
        // A publishing job without LastError is being published right now (from the agent's finish call).
        foreach (var job in publishing.Where(j => j.LastError is not null && j.NotBefore is { } due && due <= clock.UtcNow))
        {
            await publish.Handle(new PublishPullRequest(job.Id), cancellationToken).ConfigureAwait(false);
            retried++;
        }

        return retried;
    }
}
