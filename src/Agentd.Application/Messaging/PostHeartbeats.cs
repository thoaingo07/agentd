using System.Collections.Concurrent;
using System.Globalization;
using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Messaging;

/// <summary>
/// The every-minute heartbeat of active jobs. In each open thread it posts the job's status with a
/// timestamp and deletes the previous heartbeat, so the latest status is always the last message.
/// Alongside it posts usage warnings (80% / 95% of the 5-hour or weekly window) and a "no activity"
/// warning when the agent is silent too long. When a job ends, its last heartbeat is removed.
/// Returns the number of threads updated.
/// </summary>
public sealed record PostHeartbeats;

public sealed class PostHeartbeatsHandler(
    IJobRepository jobs,
    IConversationStore conversations,
    IMessagingProviderRegistry providers,
    IOutbox outbox,
    JobActivity activity,
    HeartbeatState state,
    IClock clock,
    IOptions<JobOptions> options) : ICommandHandler<PostHeartbeats, int>
{
    public async Task<Result<int>> Handle(PostHeartbeats command, CancellationToken cancellationToken)
    {
        if (providers.Enabled.Count == 0)
        {
            return 0;
        }

        var now = clock.UtcNow;
        var active = await jobs.ListByStateAsync([JobState.Running, JobState.WaitingForHuman], cancellationToken).ConfigureAwait(false);
        var updated = 0;
        foreach (var job in active)
        {
            await WarnAsync(job, now, cancellationToken).ConfigureAwait(false);
            var text = string.Create(CultureInfo.InvariantCulture, $"{JobActivity.Describe(job, activity.Get(job.Id), now)} · {now:HH:mm:ss} UTC");
            foreach (var thread in (await conversations.ListByJobAsync(job.Id, cancellationToken).ConfigureAwait(false)).Where(c => c.IsOpen))
            {
                updated += await RepostAsync(thread, new OutboundMessage(MessageKind.Progress, text), cancellationToken).ConfigureAwait(false) ? 1 : 0;
            }

            state.Active.TryAdd(job.Id.Value, 0);
        }

        // Jobs that ended since the last beat: remove their last heartbeat.
        foreach (var ended in state.Active.Keys.Where(id => active.All(j => j.Id.Value != id)).ToList())
        {
            foreach (var thread in await conversations.ListByJobAsync(new JobId(ended), cancellationToken).ConfigureAwait(false))
            {
                await RepostAsync(thread, null, cancellationToken).ConfigureAwait(false);
            }

            state.Active.TryRemove(ended, out _);
            activity.Forget(new JobId(ended));
        }

        return updated;
    }

    private async Task WarnAsync(Job job, DateTimeOffset now, CancellationToken ct)
    {
        var usage = activity.Get(job.Id).Usage;
        foreach (var (window, utilization) in activity.TakeUsageWarnings(job.Id))
        {
            var resets = usage?.ResetsAt is { } at ? string.Create(CultureInfo.InvariantCulture, $", resets at {at:HH:mm} UTC") : "";
            await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info,
                $"⚠️ **Usage at {JobActivity.Percent(utilization)} of the {window} window**{resets}."), ct).ConfigureAwait(false);
        }

        // The machine running out of disk or memory makes builds, clones and agents fail: say so in each running job's thread, once.
        if (activity.Machine is { } machine)
        {
            if (activity.ResourceWarningChanged(job.Id, "disk", machine.LowDisk) && machine.LowDisk)
            {
                await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info,
                    $"⚠️ **Low disk:** {JobActivity.Bytes(machine.DiskFree)} free of {JobActivity.Bytes(machine.DiskTotal)}. Builds and clones may fail; unused checkouts are removed hourly."), ct).ConfigureAwait(false);
            }

            if (activity.ResourceWarningChanged(job.Id, "memory", machine.LowMemory) && machine.LowMemory)
            {
                await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info,
                    $"⚠️ **Low memory:** {JobActivity.Bytes(machine.MemoryAvailable)} available of {JobActivity.Bytes(machine.MemoryTotal)}. Builds may be killed; consider fewer concurrent jobs."), ct).ConfigureAwait(false);
            }
        }

        var last = activity.Get(job.Id).LastActivityAt;
        var stuck = job.State == JobState.Running && last is { } seen && now - seen >= options.Value.StuckAfter;
        if (activity.StuckChanged(job.Id, stuck))
        {
            var snapshot = activity.Get(job.Id);
            var message = !stuck ? "✅ **Active again.**"
                : snapshot.Running
                    // One long command, not a silent agent: say what runs, that messages wait for it, and how to stop it.
                    ? $"⚠️ **The agent has been running {snapshot.LastActivity} for {JobActivity.Ago(now - last!.Value)}.** It reads your messages when this ends; " +
                      "`!pause` stops it." + (snapshot.Output?.Tail() is { Length: > 0 } tail ? $"\n```\n{tail.Replace("```", "ʼʼʼ", StringComparison.Ordinal)}\n```" : string.Empty)
                    : $"⚠️ **No activity for {JobActivity.Ago(now - last!.Value)}** (last: {snapshot.LastActivity}).";
            await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info, message), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Posts <paramref name="heartbeat"/> (null = none) and deletes the previous one. Best effort.</summary>
    private async Task<bool> RepostAsync(Conversation thread, OutboundMessage? heartbeat, CancellationToken ct)
    {
        try
        {
            var provider = providers.Resolve(thread.Provider);
            var reference = new ConversationRef(thread.Provider, thread.ExternalConversationId, thread.ExternalSpaceId);
            var previous = thread.StatusMessageId;
            if (heartbeat is not null)
            {
                var posted = await provider.SendAsync(reference, heartbeat, ct).ConfigureAwait(false);
                thread.SetStatusMessage(posted.ExternalMessageId);
            }

            if (previous is not null)
            {
                try
                {
                    await provider.DeleteAsync(new MessageRef(reference, previous), ct).ConfigureAwait(false);
                }
                catch (MessagingDeliveryException)
                {
                    // Already gone (deleted by hand, or the thread was archived): nothing to clean up.
                }
            }

            if (heartbeat is not null || previous is not null)
            {
                if (heartbeat is null)
                {
                    thread.ClearStatusMessage();
                }

                await conversations.SaveAsync(thread, ct).ConfigureAwait(false);
            }

            return heartbeat is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Jobs the heartbeat is tracking (in memory), so it can clean up when they end.</summary>
public sealed class HeartbeatState
{
    public ConcurrentDictionary<long, byte> Active { get; } = new();
}
