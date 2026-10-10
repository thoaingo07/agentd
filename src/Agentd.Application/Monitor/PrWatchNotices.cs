using Agentd.Application.Messaging;

namespace Agentd.Application.Monitor;

/// <summary>What the monitor says about a watch: kept with the watch (the Web UI shows it) and posted to its 👀 thread, if any.</summary>
internal static class PrWatchNotices
{
    public static async Task PostAsync(IPrWatchStore watches, IMessagingProviderRegistry providers, PrWatch watch, string text, CancellationToken ct)
    {
        await watches.AddMessageAsync(watch.Id, text, ct).ConfigureAwait(false);
        if (watch.ThreadId is { } thread)
        {
            await providers.Resolve(watch.Provider).SendAsync(new ConversationRef(watch.Provider, thread, watch.SpaceId), new OutboundMessage(MessageKind.Info, text), ct).ConfigureAwait(false);
        }
    }
}
