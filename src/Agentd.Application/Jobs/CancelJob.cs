using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

public sealed record CancelJob(JobId JobId, string By);

public sealed class CancelJobHandler(IJobRepository jobs, IAgentRunner runner) : ICommandHandler<CancelJob, Unit>
{
    public async Task<Result<Unit>> Handle(CancelJob command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var job = await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return DomainError.NotFound($"Job {command.JobId}");
        }

        var cancelled = job.Cancel(command.By);
        if (!cancelled.IsSuccess)
        {
            return cancelled.Error;
        }

        if (runner.IsRunning(job.Id))
        {
            runner.Cancel(job.Id);
        }

        var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Unit.Value : saved.Error;
    }
}
