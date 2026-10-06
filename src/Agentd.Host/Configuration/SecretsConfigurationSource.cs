namespace Agentd.Host.Configuration;

/// <summary>The decrypted secrets as configuration (<c>AzureDevOps:Pat</c> → <c>Agentd:AzureDevOps:Pat</c>).</summary>
internal sealed class SecretsConfigurationSource(ConfigHome home, TextWriter? warnings = null) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(home, warnings ?? Console.Error);

    private sealed class Provider(ConfigHome home, TextWriter warnings) : ConfigurationProvider
    {
        public override void Load()
        {
            var (values, unreadable) = new SecretStore(home).Load();
            Data = values.ToDictionary(kv => SecretStore.ConfigKey(kv.Key), kv => (string?)kv.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var key in unreadable)
            {
                // The name only, never the value; `agentd doctor` reports it too.
                warnings.WriteLine($"warning: secret '{key}' can't be decrypted (was {home.Keys} replaced?); skipped. Set it again with: agentd secrets set {key}");
            }
        }
    }
}
