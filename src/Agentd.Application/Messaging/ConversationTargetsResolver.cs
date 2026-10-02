using Agentd.Domain.Messaging;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Messaging;

/// <summary>Which providers a job opens conversations on, and the <c>chat:</c> tags that were ignored.</summary>
public sealed record ConversationTargets(IReadOnlyList<ProviderKey> Providers, IReadOnlyList<string> IgnoredTags);

/// <summary>
/// Picks a job's providers: <c>chat:&lt;provider&gt;</c> work item tags win; otherwise
/// <see cref="MessagingOptions.DefaultProviders"/>, or every enabled provider when none are listed.
/// Tags naming a provider that isn't enabled are ignored (and reported). Per-repo kit overrides come in Phase 4.
/// </summary>
public sealed class ConversationTargetsResolver(IOptions<MessagingOptions> options)
{
    private const string TagPrefix = "chat:";

    public ConversationTargets Resolve(IReadOnlyList<string> workItemTags)
    {
        ArgumentNullException.ThrowIfNull(workItemTags);
        var o = options.Value;
        var enabled = o.EnabledKeys();
        var chosen = new List<ProviderKey>();
        var ignored = new List<string>();
        foreach (var tag in workItemTags.Where(t => t.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            var key = enabled.FirstOrDefault(k => string.Equals(k.Value, tag[TagPrefix.Length..].Trim(), StringComparison.OrdinalIgnoreCase));
            if (key.Value is null)
            {
                ignored.Add(tag);
            }
            else if (!chosen.Contains(key))
            {
                chosen.Add(key);
            }
        }

        if (chosen.Count == 0)
        {
            chosen = o.DefaultProviders.Count == 0
                ? [.. enabled]
                : enabled.Where(k => o.DefaultProviders.Contains(k.Value, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        return new ConversationTargets(chosen, ignored);
    }
}
