using Agentd.Domain.Messaging;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Messaging;

/// <summary>The registered providers (from DI) that are enabled in <see cref="MessagingOptions"/>, in configuration order.</summary>
public sealed class MessagingProviderRegistry(IEnumerable<IMessagingProvider> providers, IOptions<MessagingOptions> options) : IMessagingProviderRegistry
{
    private readonly Lazy<IReadOnlyList<IMessagingProvider>> _enabled = new(() =>
    {
        var registered = providers.ToList();
        return options.Value.EnabledKeys()
            .Select(key => registered.FirstOrDefault(p => p.Key == key))
            .OfType<IMessagingProvider>()
            .ToList();
    });

    public IReadOnlyList<IMessagingProvider> Enabled => _enabled.Value;

    public IMessagingProvider Resolve(ProviderKey key) =>
        Enabled.FirstOrDefault(p => p.Key == key) ?? throw new InvalidOperationException($"Messaging provider '{key}' is not enabled.");
}
