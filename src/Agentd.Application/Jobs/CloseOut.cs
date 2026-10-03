using Agentd.Application.Abstractions;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

/// <summary>Ask the developer to delete (or keep) the thread once the work item is completely done.</summary>
public sealed record RequestCloseOut(JobId JobId);

public sealed class RequestCloseOutHandler(IJobRepository jobs) : ICommandHandler<RequestCloseOut, Unit>
{
    public const string DeleteLabel = "🗑 Delete thread";
    public const string KeepLabel = "📦 Keep (archive)";

    public async Task<Result<Unit>> Handle(RequestCloseOut command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var job = await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return DomainError.NotFound($"Job {command.JobId}");
        }

        var asked = job.RequestCloseOut("🧹 **All done.** Delete this thread?", [DeleteLabel, KeepLabel]);
        if (!asked.IsSuccess)
        {
            return asked.Error;
        }

        var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Unit.Value : saved.Error;
    }
}

/// <summary>The developer's answer to the close-out question (handled by agentd, no agent turn).</summary>
public sealed record AnswerCloseOut(JobId JobId, string Reply);

/// <summary>"1"/"delete" deletes every thread of the job, "2"/"keep" archives them; anything else re-asks.</summary>
public sealed class AnswerCloseOutHandler(
    IJobRepository jobs,
    IConversationStore conversations,
    IMessagingProviderRegistry providers,
    IOutbox outbox,
    IClock clock) : ICommandHandler<AnswerCloseOut, bool>
{
    private static readonly HashSet<string> s_delete = new(StringComparer.OrdinalIgnoreCase) { "1", "delete", "delete it", "yes", RequestCloseOutHandler.DeleteLabel };
    private static readonly HashSet<string> s_keep = new(StringComparer.OrdinalIgnoreCase) { "2", "keep", "no", "archive", RequestCloseOutHandler.KeepLabel };

    public async Task<Result<bool>> Handle(AnswerCloseOut command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var job = await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null || job.Handoff != HandoffStatus.Closing || job.State != JobState.WaitingForHuman)
        {
            return false;
        }

        var reply = command.Reply.Trim().TrimEnd('.', '!');
        var delete = s_delete.Contains(reply);
        if (!delete && !s_keep.Contains(reply))
        {
            await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info, "Please answer `1` to delete this thread or `2` to keep it (archived)."), cancellationToken).ConfigureAwait(false);
            return true;
        }

        var closed = job.CloseOut(delete);
        if (!closed.IsSuccess || !(await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false)).IsSuccess)
        {
            return false;
        }

        foreach (var thread in (await conversations.ListByJobAsync(job.Id, cancellationToken).ConfigureAwait(false)).Where(c => c.IsOpen))
        {
            try
            {
                var provider = providers.Resolve(thread.Provider);
                var reference = new ConversationRef(thread.Provider, thread.ExternalConversationId, thread.ExternalSpaceId);
                if (delete)
                {
                    await provider.DeleteConversationAsync(reference, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await provider.CloseConversationAsync(reference, "📦 Archived. This work item is done.", cancellationToken).ConfigureAwait(false);
                }

                thread.Close(clock.UtcNow);
                await conversations.SaveAsync(thread, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best effort: a thread that is already gone needs no clean-up.
            }
        }

        return true;
    }
}
