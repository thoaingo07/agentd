namespace Agentd.Host.Configuration;

/// <summary>
/// Which configuration source wins for a key (see <see cref="AgentdConfiguration.AddConfigHome"/>): Settings says so when
/// a source above agentd.json or secrets.json supplies the running value, since saving there then changes nothing.
/// </summary>
internal static class ConfigurationLayers
{
    /// <summary>What supplies <paramref name="key"/> (a full path, <c>Agentd:…</c>) over agentd.json (<paramref name="secret"/>: over secrets.json), or null.</summary>
    public static string? OverriddenBy(IConfiguration? configuration, string key, bool secret)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return null;
        }

        var providers = root.Providers.ToList();
        var secrets = providers.FindIndex(p => p is SecretsConfigurationSource.Provider);
        if (secrets < 0)
        {
            return null;
        }

        // agentd.json is the source just before secrets.json.
        var layer = secret ? secrets : secrets - 1;
        var winner = providers.FindLastIndex(p => p.TryGet(key, out var value) && !string.IsNullOrEmpty(value));
        return winner <= layer ? null : providers[winner] switch
        {
            KeyVaultConfigurationSource.Provider vault => $"Key Vault ({vault.Vault})",
            SecretsConfigurationSource.Provider => "the secret store",
            _ => "an environment variable or the command line",
        };
    }
}
