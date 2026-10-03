using Agentd.Application.Messaging;

namespace Agentd.Infrastructure.Tests.Messaging.Contract;

/// <summary>A platform-native message as the provider would receive it (no network involved).</summary>
public sealed record NativeMessage(string Id, string ConversationId, string Text, bool FromBot = false);

/// <summary>
/// What a provider's contract tests need: the provider wired to a fake transport, the text it put on
/// the wire, a way to inject native inbound messages and break the transport, and the platform's
/// definition of "active" markup.
/// </summary>
public interface IProviderTestHarness : IDisposable
{
    IMessagingProvider Provider { get; }

    /// <summary>The text of every message sent or edited, in wire format, oldest first.</summary>
    IReadOnlyList<string> WireTexts { get; }

    /// <summary>Make every following transport call fail like an outage.</summary>
    void FailTransport();

    /// <summary>The platform's syntax for a command, e.g. <c>!run 1234</c> or <c>/run 1234</c>.</summary>
    string CommandText(string name, params string[] args);

    /// <summary>Delivers native messages through the provider's real inbound path; returns what reached the sink.</summary>
    Task<IReadOnlyList<InboundMessage>> DeliverAsync(params NativeMessage[] messages);

    /// <summary>Markup in <paramref name="wireText"/> the platform would act on (pings, links to unsafe schemes, …).</summary>
    IReadOnlyList<string> ActiveMarkup(string wireText);

    /// <summary>Structural guarantees per sent message (e.g. "mentions disabled"); empty when all hold.</summary>
    IReadOnlyList<string> UnsafeRequests();
}
