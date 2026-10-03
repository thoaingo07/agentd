using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;

namespace Agentd.Application.Jobs;

/// <summary>A developer wrote to a job (chat reply, button press, later the Web UI).</summary>
public sealed record SubmitDeveloperMessage(JobId JobId, string Text, string From, ProviderKey? Via = null);

/// <summary>What happened to the message.</summary>
public enum DeveloperMessageOutcome
{
    /// <summary>The job was waiting for a reply and is running again.</summary>
    Resumed,

    /// <summary>The agent is running; the message is delivered as its next turn.</summary>
    Queued,

    /// <summary>The job doesn't take messages in its current state (finished, queued, publishing).</summary>
    NotAccepted,
}

/// <summary>
/// Delivers a developer message to a job. The first reply to a waiting job resumes it; a reply that
/// loses the race (version conflict) is reloaded and queued for the next turn instead.
/// </summary>
public sealed class SubmitDeveloperMessageHandler(IJobRepository jobs) : ICommandHandler<SubmitDeveloperMessage, DeveloperMessageOutcome>
{
    private const int MaxAttempts = 5;

    public async Task<Result<DeveloperMessageOutcome>> Handle(SubmitDeveloperMessage command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        for (var attempt = 1; ; attempt++)
        {
            var job = await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                return DomainError.NotFound($"Job {command.JobId}");
            }

            if (job.State is not (JobState.WaitingForHuman or JobState.Running))
            {
                return DeveloperMessageOutcome.NotAccepted;
            }

            var resumes = job.State == JobState.WaitingForHuman;
            if (resumes && job.PlanStatus == PlanStatus.Pending && SubmitPlanHandler.IsApproval(command.Text))
            {
                job.ApprovePlan(command.From);
            }

            var accepted = job.ResumeWith(command.Text, command.From, command.Via);
            if (!accepted.IsSuccess)
            {
                return accepted.Error;
            }

            var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            if (saved.IsSuccess)
            {
                return resumes ? DeveloperMessageOutcome.Resumed : DeveloperMessageOutcome.Queued;
            }

            if (saved.Error.Code != "conflict" || attempt == MaxAttempts)
            {
                return saved.Error;
            }
        }
    }
}
