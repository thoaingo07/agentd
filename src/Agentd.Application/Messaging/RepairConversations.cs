using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;

namespace Agentd.Application.Messaging;

/// <summary>
/// Retry opening conversations that failed when a job started (a provider was down): for running and
/// waiting jobs with fewer open conversations than enabled providers, re-run the opening, which is
/// idempotent and respects the job's <c>chat:</c> tags. Returns how many jobs were checked against
/// their work item.
/// </summary>
public sealed record RepairConversations;

public sealed class RepairConversationsHandler(
    IJobRepository jobs,
    IConversationStore conversations,
    IWorkItemSource workItems,
    IMessagingProviderRegistry providers,
    MessagingService messaging) : ICommandHandler<RepairConversations, int>
{
    public async Task<Result<int>> Handle(RepairConversations command, CancellationToken cancellationToken)
    {
        var enabled = providers.Enabled.Count;
        if (enabled == 0)
        {
            return 0;
        }

        var checkedJobs = 0;
        foreach (var job in await jobs.ListByStateAsync([JobState.Running, JobState.WaitingForHuman], cancellationToken).ConfigureAwait(false))
        {
            var open = (await conversations.ListByJobAsync(job.Id, cancellationToken).ConfigureAwait(false)).Count(c => c.IsOpen);
            if (open >= enabled)
            {
                continue;
            }

            // Tags may limit the job to fewer providers; the work item tells.
            if (await workItems.GetAsync(job.WorkItemId.Value, cancellationToken).ConfigureAwait(false) is { } item)
            {
                await messaging.OpenConversationsAsync(job, item, cancellationToken).ConfigureAwait(false);
                checkedJobs++;
            }
        }

        return checkedJobs;
    }
}
