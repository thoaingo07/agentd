using System.Text.Json;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Messaging;

namespace Agentd.Application.Messaging;

/// <summary>
/// Opens a job's conversations when it starts: one per target provider (T2.3 routing), called
/// directly rather than through the outbox because later messages need the conversation id. A
/// provider failure never fails the job: it is recorded as a <c>MessagingDegraded</c> event, and the
/// method itself does not throw (except on cancellation).
/// </summary>
public sealed class MessagingService(
    IMessagingProviderRegistry providers,
    ConversationTargetsResolver targets,
    IConversationStore conversations,
    IEventStore events,
    IClock clock)
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public async Task OpenConversationsAsync(Job job, WorkItemDetails item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(item);
        var resolved = targets.Resolve(item.Tags);
        foreach (var tag in resolved.IgnoredTags)
        {
            await RecordAsync(job, "MessagingTagIgnored", new { tag, reason = "no enabled provider has that name" }, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<Conversation> existing;
        try
        {
            existing = await conversations.ListByJobAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Messaging must never stop the job from running; the dispatcher retries opening later.
            return;
        }

        foreach (var key in resolved.Providers.Where(k => !existing.Any(c => c.Provider == k && c.IsOpen)))
        {
            try
            {
                var provider = providers.Resolve(key);
                var spec = new ConversationSpec(job.Id, job.WorkItemId, job.Title, job.Repository, MessageCatalog.Starter(job, item, item.Url));
                var opened = await provider.OpenConversationAsync(spec, cancellationToken).ConfigureAwait(false);
                var conversation = Conversation.Open(job.Id, key, opened.ExternalConversationId, opened.ExternalSpaceId, provider.GetLink(opened), existing, clock.UtcNow);
                var saved = conversation.IsSuccess
                    ? await conversations.AddAsync(conversation.Value, cancellationToken).ConfigureAwait(false)
                    : Result.Fail(conversation.Error);
                if (!saved.IsSuccess)
                {
                    await RecordAsync(job, "MessagingDegraded", new { provider = key.Value, error = saved.Error.Message }, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordAsync(job, "MessagingDegraded", new { provider = key.Value, error = ex.Message }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RecordAsync(Job job, string type, object payload, CancellationToken ct)
    {
        try
        {
            await events.AppendAsync(job.Id, type, JsonSerializer.Serialize(payload, s_json), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: the event log being unavailable must not stop the job either.
        }
    }
}
