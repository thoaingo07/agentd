using Microsoft.Extensions.Primitives;

namespace Agentd.Host.Configuration;

/// <summary>
/// Exposes another provider's keys under a section, so <c>config/agentd.json</c> and <c>AGENTD_*</c>
/// variables can use the schema of the <c>Agentd</c> section without repeating "Agentd".
/// </summary>
internal sealed class PrefixedConfigurationProvider(IConfigurationProvider inner, string section) : IConfigurationProvider, IDisposable
{
    private readonly string _prefix = section + ConfigurationPath.KeyDelimiter;

    public bool TryGet(string key, out string? value)
    {
        if (key.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
        {
            return inner.TryGet(key[_prefix.Length..], out value);
        }

        value = null;
        return false;
    }

    public void Set(string key, string? value)
    {
        if (key.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
        {
            inner.Set(key[_prefix.Length..], value);
        }
    }

    public IChangeToken GetReloadToken() => inner.GetReloadToken();

    public void Load() => inner.Load();

    public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
    {
        if (parentPath is null)
        {
            return inner.GetChildKeys([], null).Any() ? earlierKeys.Append(section) : earlierKeys;
        }

        if (string.Equals(parentPath, section, StringComparison.OrdinalIgnoreCase))
        {
            return inner.GetChildKeys(earlierKeys, null);
        }

        return parentPath.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
            ? inner.GetChildKeys(earlierKeys, parentPath[_prefix.Length..])
            : earlierKeys;
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();
}

internal sealed class PrefixedConfigurationSource(IConfigurationSource inner, string section) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new PrefixedConfigurationProvider(inner.Build(builder), section);
}
