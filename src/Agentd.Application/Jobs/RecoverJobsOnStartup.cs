using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;

namespace Agentd.Application.Jobs;

/// <summary>After a restart: resume running jobs, finish publishing, fail jobs interrupted mid-preparation.</summary>
public sealed record RecoverJobsOnStartup;

/// <summary>Agent runs to resume, and how many publishing jobs were retried.</summary>
public sealed record RecoveryPlan(IReadOnlyList<AgentRunRequest> Resume, int RepublishedCount, int FailedCount);

public sealed class RecoverJobsOnStartupHandler(
    IJobRepository jobs,
    IAgentRunner runner,
    ICommandHandler<PublishPullRequest, PullRequestRef> publish) : ICommandHandler<RecoverJobsOnStartup, RecoveryPlan>
{
    public async Task<Result<RecoveryPlan>> Handle(RecoverJobsOnStartup command, CancellationToken cancellationToken)
    {
        var active = await jobs.ListByStateAsync([JobState.Preparing, JobState.Running, JobState.Publishing], cancellationToken).ConfigureAwait(false);
        var resume = new List<AgentRunRequest>();
        int republished = 0, failed = 0;

        foreach (var job in active)
        {
            switch (job.State)
            {
                case JobState.Running when !runner.IsRunning(job.Id) && job is { Worktree: { } wt, Session: { } session }:
                    if (job.MarkRecovered().IsSuccess && (await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false)).IsSuccess)
                    {
                        resume.Add(new AgentRunRequest(job.Id, job.WorkItemId, wt, session, TaskPromptBuilder.ResumePrompt, Resume: true));
                    }

                    break;

                case JobState.Publishing:
                    await publish.Handle(new PublishPullRequest(job.Id), cancellationToken).ConfigureAwait(false);
                    republished++;
                    break;

                case JobState.Preparing:
                    job.Fail("agentd restarted while the job was being prepared. Retry it to start again.");
                    await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
                    failed++;
                    break;
            }
        }

        return new RecoveryPlan(resume, republished, failed);
    }
}
