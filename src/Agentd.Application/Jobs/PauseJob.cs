using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

/// <summary>Pause a job now (stopping its agent); <see cref="ResumeJob"/> continues it later in the same session.</summary>
public sealed record PauseJob(JobId JobId, string By);

public sealed record ResumeJob(JobId JobId, string By);

public sealed class PauseJobHandler(IJobRepository jobs, IAgentRunner runner) : ICommandHandler<PauseJob, Unit>
{
    public async Task<Result<Unit>> Handle(PauseJob command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return DomainError.NotFound($"Job {command.JobId}");
        }

        var paused = job.Pause(command.By);
        if (!paused.IsSuccess)
        {
            return paused.Error;
        }

        var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            return saved.Error;
        }

        // Saved first: when the stopped agent exits, its handler sees Paused and keeps everything.
        if (runner.IsRunning(job.Id))
        {
            runner.Cancel(job.Id);
        }

        return Unit.Value;
    }
}

public sealed class ResumeJobHandler(IJobRepository jobs) : ICommandHandler<ResumeJob, Unit>
{
    public async Task<Result<Unit>> Handle(ResumeJob command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return DomainError.NotFound($"Job {command.JobId}");
        }

        var resumed = job.Resume(command.By);
        if (!resumed.IsSuccess)
        {
            return resumed.Error;
        }

        var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Unit.Value : saved.Error;
    }
}
