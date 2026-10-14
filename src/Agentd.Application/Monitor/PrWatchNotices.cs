using Agentd.Application.Inbox;
using Agentd.Application.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Application.Monitor;

/// <summary>What the monitor says about a watch: kept with the watch (the Web UI shows it) and posted to its 👀 thread, if any.</summary>
internal static class PrWatchNotices
{
    public static async Task PostAsync(IPrWatchStore watches, IMessagingProviderRegistry providers, PrWatch watch, string text, CancellationToken ct,
        Inbox.IInbox? inbox = null, ILogger? logger = null)
    {
        await watches.AddMessageAsync(watch.Id, text, ct).ConfigureAwait(false);
        await inbox.TryAddAsync(new Inbox.InboxEntry(Inbox.InboxSources.Watch, watch.Id, $"👀 PR !{watch.PullRequestId} {watch.Title}", watch.WatchedBy,
            new OutboundMessage(MessageKind.Info, text)), logger ?? NullLogger.Instance, ct).ConfigureAwait(false);
        if (watch.ThreadId is { } thread)
        {
            // Its chat may have been switched off since: the notice is kept with the watch and in the Inbox all the same.
            if (providers.TryResolve(watch.Provider) is not { } chat)
            {
                return;
            }

            await chat.SendAsync(new ConversationRef(watch.Provider, thread, watch.SpaceId), new OutboundMessage(MessageKind.Info, text), ct).ConfigureAwait(false);
        }
    }
}
