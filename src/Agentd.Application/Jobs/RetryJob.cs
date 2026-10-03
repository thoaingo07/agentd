using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

/// <summary>Queue a failed job again (a new attempt).</summary>
public sealed record RetryJob(JobId JobId);

public sealed class RetryJobHandler(IJobRepository jobs) : ICommandHandler<RetryJob, int>
{
    public async Task<Result<int>> Handle(RetryJob command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var job = await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return DomainError.NotFound($"Job {command.JobId}");
        }

        var retried = job.Retry();
        if (!retried.IsSuccess)
        {
            return retried.Error;
        }

        var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? job.Attempt : saved.Error;
    }
}
