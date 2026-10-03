using Agentd.Domain.Messaging;

namespace Agentd.Application.Messaging;

/// <summary>
/// A chat platform (Discord first). Implementations live in <c>Infrastructure.Messaging.*</c>; the
/// Application layer only sees this port and the neutral message model (docs/architect/messaging-providers.md §3).
/// </summary>
public interface IMessagingProvider
{
    /// <summary>The provider's key, e.g. <c>discord</c>.</summary>
    ProviderKey Key { get; }

    /// <summary>What the platform supports; the messaging service adapts to it.</summary>
    MessagingCapabilities Capabilities { get; }

    /// <summary>Opens a thread (or topic) for a job and posts its opening message.</summary>
    Task<ConversationRef> OpenConversationAsync(ConversationSpec spec, CancellationToken cancellationToken);

    /// <summary>Posts a message; it must already fit <see cref="MessagingCapabilities.MaxMessageLength"/>.</summary>
    Task<MessageRef> SendAsync(ConversationRef conversation, OutboundMessage message, CancellationToken cancellationToken);

    /// <summary>Replaces a message's content (live progress). A no-op when editing is not supported.</summary>
    Task EditAsync(MessageRef message, OutboundMessage replacement, CancellationToken cancellationToken);

    /// <summary>Deletes a message (the previous heartbeat). A no-op when deleting is not supported.</summary>
    Task DeleteAsync(MessageRef message, CancellationToken cancellationToken);

    /// <summary>Archives or locks the thread, with a closing note.</summary>
    Task CloseConversationAsync(ConversationRef conversation, string reason, CancellationToken cancellationToken);

    /// <summary>A link that opens the conversation in the platform's client, when it has one.</summary>
    Uri? GetLink(ConversationRef conversation);

    /// <summary>Connectivity and credentials check (dashboard health, <c>agentd doctor</c>).</summary>
    Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken);
}

/// <summary>The enabled providers, by key.</summary>
public interface IMessagingProviderRegistry
{
    /// <summary>Providers enabled in configuration, in configuration order.</summary>
    IReadOnlyList<IMessagingProvider> Enabled { get; }

    /// <summary>The enabled provider with this key; throws when it is not enabled.</summary>
    IMessagingProvider Resolve(ProviderKey key);
}

/// <summary>What provider listeners call for every inbound message (implemented by the inbound use case).</summary>
public interface IInboundMessageSink
{
    /// <summary>Handles one inbound message (idempotent per provider message id).</summary>
    Task HandleAsync(InboundMessage message, CancellationToken cancellationToken);
}
