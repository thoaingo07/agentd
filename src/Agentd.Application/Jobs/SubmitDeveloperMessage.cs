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

    /// <summary>The developer declined the knowledge sync; the job is done.</summary>
    HandoffDeclined,

    /// <summary>The PR was in review: the message started a fix round, like a review comment.</summary>
    FixRound,

    /// <summary>The PR is already merged: no fix round (and no new PR); the message gets a talk-only answer.</summary>
    Merged,
}

/// <summary>
/// Delivers a developer message to a job. The first reply to a waiting job resumes it; a reply that
/// loses the race (version conflict) is reloaded and queued for the next turn instead.
/// </summary>
public sealed class SubmitDeveloperMessageHandler(
    IJobRepository jobs,
    ICommandHandler<RequestCloseOut, Unit>? closeOut = null,
    IPullRequestService? pullRequests = null,
    IRepositoryRegistry? repositories = null)
    : ICommandHandler<SubmitDeveloperMessage, DeveloperMessageOutcome>
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

            if (job.State == JobState.InReview && await IsMergedAsync(job, cancellationToken).ConfigureAwait(false))
            {
                // Merged between two review polls: a fix round would publish a second PR. The review loop ends the job.
                return DeveloperMessageOutcome.Merged;
            }

            if (job.State == JobState.InReview)
            {
                // Feedback on the open PR from chat counts like a review comment: the agent resumes to address it.
                var started = job.StartFixRound([$"{command.From} (in chat): {command.Text}"], []);
                var fixSaved = started.IsSuccess ? await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false) : Result.Fail(started.Error);
                if (fixSaved.IsSuccess)
                {
                    return DeveloperMessageOutcome.FixRound;
                }

                if (fixSaved.Error.Code != "conflict" || attempt == MaxAttempts)
                {
                    return fixSaved.Error;
                }

                continue;
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

            if (resumes && job.Handoff == HandoffStatus.Proposing && ProposeKnowledgeHandler.IsDecline(command.Text))
            {
                var declined = job.DeclineHandoff(command.From);
                var stored = declined.IsSuccess ? await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false) : Result.Fail(declined.Error);
                if (stored.IsSuccess)
                {
                    if (closeOut is not null)
                    {
                        await closeOut.Handle(new RequestCloseOut(job.Id), cancellationToken).ConfigureAwait(false);
                    }

                    return DeveloperMessageOutcome.HandoffDeclined;
                }

                if (stored.Error.Code != "conflict" || attempt == MaxAttempts)
                {
                    return stored.Error;
                }

                continue;
            }

            if (resumes && job.Handoff == HandoffStatus.Proposing && ProposeKnowledgeHandler.IsAgreement(command.Text))
            {
                job.AgreeHandoff(command.From);
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

    private async Task<bool> IsMergedAsync(Job job, CancellationToken ct)
    {
        if (pullRequests is null || repositories is null || job.PullRequest is not { } url
            || ReviewPullRequestsHandler.PullRequestId(url.Value) is not { } id
            || await repositories.GetAsync(job.Repository, ct).ConfigureAwait(false) is not { } repository)
        {
            return false;
        }

        try
        {
            return await pullRequests.GetStatusAsync(repository, id, ct).ConfigureAwait(false) == PullRequestStatus.Completed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;   // can't tell (ADO unreachable): treat it as review feedback, as before
        }
    }
}
