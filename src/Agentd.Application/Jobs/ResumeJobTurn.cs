using Agentd.Application.Abstractions;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;

namespace Agentd.Application.Jobs;

/// <summary>
/// Find a running job with queued developer replies and no live agent (not in <see cref="Busy"/>),
/// take its replies and return a resume turn in the same Claude session. Null when there is none.
/// </summary>
public sealed record ResumeJobTurn(IReadOnlyCollection<long> Busy);

public sealed class ResumeJobTurnHandler(IJobRepository jobs, IOutbox outbox) : ICommandHandler<ResumeJobTurn, AgentRunRequest?>
{
    public async Task<Result<AgentRunRequest?>> Handle(ResumeJobTurn command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var running = await jobs.ListByStateAsync([JobState.Running], cancellationToken).ConfigureAwait(false);
        foreach (var job in running.Where(j => j.PendingMessages.Count > 0 && !command.Busy.Contains(j.Id.Value)))
        {
            if (job is not { Worktree: { } worktree, Session: { } session })
            {
                continue;
            }

            var messages = job.TakePendingMessages();
            // The version check makes the hand-off exactly-once: a concurrent change sends us to the next job.
            if ((await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false)).IsSuccess)
            {
                await outbox.TryEnqueueAsync(job.Id, MessageCatalog.Resumed(messages.Count), cancellationToken).ConfigureAwait(false);
                return new AgentRunRequest(job.Id, job.WorkItemId, worktree, session, TaskPromptBuilder.Replies(messages), Resume: true);
            }
        }

        return Result.Success<AgentRunRequest?>(null);
    }
}
