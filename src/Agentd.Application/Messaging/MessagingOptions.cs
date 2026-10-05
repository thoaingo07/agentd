using Agentd.Domain.Messaging;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Messaging;

/// <summary>
/// Configuration section <c>Agentd:Messaging</c>. Provider-specific settings (IDs, the bot token) are
/// bound and validated by each provider; this holds what routing needs.
/// </summary>
public sealed class MessagingOptions
{
    public const string Section = "Agentd:Messaging";

    /// <summary>Providers that get a conversation for every job. Empty = every enabled provider.</summary>
    public IList<string> DefaultProviders { get; } = [];

    /// <summary>Providers by name (e.g. <c>Discord</c>); the provider key is the lower-cased name.</summary>
    public IDictionary<string, MessagingProviderSettings> Providers { get; } =
        new Dictionary<string, MessagingProviderSettings>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keys of the enabled providers, in configuration order.</summary>
    public IReadOnlyList<ProviderKey> EnabledKeys() =>
        Providers.Where(p => p.Value.Enabled)
            .Select(p => ProviderKey.Create(p.Key.ToLowerInvariant()))
            .Where(k => k.IsSuccess)
            .Select(k => k.Value)
            .ToList();
}

public sealed class MessagingProviderSettings
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Accept messages from anyone who can post in this provider's agentd channel and its threads, not just
    /// the users in <c>Agentd:Users</c>. Then who may post there (the chat's own channel permissions) is the
    /// access control. A stranger acts under their display name with the Operator role; listed users keep
    /// their name and roles, and a listed user marked inactive stays blocked. Off by default.
    /// </summary>
    public bool AllowEveryone { get; set; }
}

/// <summary>Fails startup on messaging configuration that cannot work.</summary>
public sealed class MessagingOptionsValidator : IValidateOptions<MessagingOptions>
{
    public ValidateOptionsResult Validate(string? name, MessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        foreach (var provider in options.Providers.Keys.Where(k => !ProviderKey.Create(k.ToLowerInvariant()).IsSuccess))
        {
            errors.Add($"Agentd:Messaging:Providers:{provider}: the name must be letters, digits and dashes.");
        }

        var enabled = options.EnabledKeys().Select(k => k.Value).ToHashSet();
        foreach (var provider in options.DefaultProviders.Where(p => !enabled.Contains(p.ToLowerInvariant())))
        {
            errors.Add($"Agentd:Messaging:DefaultProviders: '{provider}' is not an enabled provider.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
