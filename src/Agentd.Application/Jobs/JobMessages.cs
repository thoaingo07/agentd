using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;

namespace Agentd.Application.Jobs;

/// <summary>What became of a message to a job (also the chat's <c>inbound_messages.outcome</c>).</summary>
public static class JobMessageOutcomes
{
    public const string Permission = "permission";
    public const string CloseOut = "close_out";
    public const string FollowUp = "follow_up";
    public const string NotAccepted = "not_accepted";
    public const string Resumed = "resumed";
    public const string Queued = "queued";
    public const string FixRound = "fix_round";
    public const string HandoffDeclined = "handoff_declined";
}

/// <summary>
/// A message to a job, the same from its chat thread and from the Web UI: it answers an open permission request, or the
/// close-out question, or goes to the job (a reply, the next turn, a fix round in review, the plan's approval, the
/// hand-off's answer); after the merge it's a talk-only follow-up.
/// </summary>
/// <remarks>Follow-ups after the merge need both <see cref="JobFollowUps"/> and the job repository.</remarks>
public sealed class JobMessages(
    ICommandHandler<SubmitDeveloperMessage, DeveloperMessageOutcome> submit,
    ICommandHandler<AnswerCloseOut, bool> closeOut,
    ICommandHandler<Permissions.PermissionAnswer, bool>? permissions = null,
    JobFollowUps? followUps = null,
    IJobRepository? jobs = null)
{
    /// <returns>One of <see cref="JobMessageOutcomes"/>.</returns>
    public async Task<Result<string>> RouteAsync(JobId jobId, string text, string from, ProviderKey? via, CancellationToken cancellationToken)
    {
        // "1"–"4" / allow / always / deny answer an open permission request first (the agent is waiting on it).
        if (permissions is not null
            && (await permissions.Handle(new Permissions.PermissionAnswer(jobId, text, from), cancellationToken).ConfigureAwait(false)) is { IsSuccess: true, Value: true })
        {
            return JobMessageOutcomes.Permission;
        }

        if ((await closeOut.Handle(new AnswerCloseOut(jobId, text), cancellationToken).ConfigureAwait(false)) is { IsSuccess: true, Value: true })
        {
            return JobMessageOutcomes.CloseOut;
        }

        var result = await submit.Handle(new SubmitDeveloperMessage(jobId, text, from, via), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        if (result.Value is DeveloperMessageOutcome.Merged or DeveloperMessageOutcome.NotAccepted
            && followUps is not null && jobs is not null && await jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false) is { } finished
            && JobFollowUps.Accepts(finished) && (result.Value == DeveloperMessageOutcome.Merged || finished.State == JobState.Done))
        {
            // After the merge: talk only (no fix round, no new PR). The job's session answers read-only.
            followUps.Ask(finished, from, text);
            return JobMessageOutcomes.FollowUp;
        }

        return result.Value switch
        {
            DeveloperMessageOutcome.NotAccepted or DeveloperMessageOutcome.Merged => JobMessageOutcomes.NotAccepted,
            DeveloperMessageOutcome.Resumed => JobMessageOutcomes.Resumed,
            DeveloperMessageOutcome.HandoffDeclined => JobMessageOutcomes.HandoffDeclined,
            DeveloperMessageOutcome.FixRound => JobMessageOutcomes.FixRound,
            _ => JobMessageOutcomes.Queued,
        };
    }
}
