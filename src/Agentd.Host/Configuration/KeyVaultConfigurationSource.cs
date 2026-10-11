using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace Agentd.Host.Configuration;

/// <summary>
/// Secrets from Azure Key Vault (<c>Agentd:KeyVault:Uri</c>), read once at start, between <c>secrets.json</c> and the
/// environment: a vault value overrides the local stores, an <c>AGENTD_*</c> variable still overrides the vault. Names use
/// <c>--</c> for <c>:</c> (Key Vault allows letters, digits and dashes): <c>Artifacts--S3--SecretAccessKey</c> →
/// <c>Agentd:Artifacts:S3:SecretAccessKey</c>, <c>ConnectionStrings--agentd</c> → <c>ConnectionStrings:agentd</c>. With
/// <c>Agentd:KeyVault:Prefix</c> (e.g. <c>agentd-</c>) only names that start with it are read, so a shared vault works.
/// Sign-in is <see cref="DefaultAzureCredential"/>: a managed identity, a service principal (<c>AZURE_TENANT_ID</c>,
/// <c>AZURE_CLIENT_ID</c>, <c>AZURE_CLIENT_SECRET</c>) or <c>az login</c>. <c>Agentd:KeyVault:TenantId</c> names the vault's
/// directory when it isn't the sign-in's default (e.g. <c>az login</c>'s default subscription is another tenant's). A vault that can't be read is a warning (names,
/// never values), so <c>agentd doctor</c> still runs.
/// </summary>
internal sealed class KeyVaultConfigurationSource(SecretClient client, string? prefix, string vault, TextWriter? warnings = null) : IConfigurationSource
{
    public const string UriKey = "Agentd:KeyVault:Uri";
    public const string PrefixKey = "Agentd:KeyVault:Prefix";
    public const string TenantIdKey = "Agentd:KeyVault:TenantId";

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(client, prefix, vault, warnings ?? Console.Error);

    /// <summary>The vault named in <paramref name="configuration"/>, signed in with <see cref="DefaultAzureCredential"/>; null when none is set.</summary>
    public static KeyVaultConfigurationSource? From(IConfiguration configuration)
    {
        if (configuration[UriKey] is not { Length: > 0 } uri)
        {
            return null;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var vault) || vault.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{UriKey} must be the vault's https URL, e.g. https://my-vault.vault.azure.net/.");
        }

        var credential = new DefaultAzureCredential(CredentialOptions(configuration));
        return new KeyVaultConfigurationSource(new SecretClient(vault, credential), configuration[PrefixKey], vault.Host);
    }

    /// <summary>How to sign in: never a browser prompt; in <see cref="TenantIdKey"/>'s directory when set.</summary>
    internal static DefaultAzureCredentialOptions CredentialOptions(IConfiguration configuration)
    {
        var options = new DefaultAzureCredentialOptions { ExcludeInteractiveBrowserCredential = true };
        if (configuration[TenantIdKey] is { Length: > 0 } tenant)
        {
            options.TenantId = tenant;
        }

        return options;
    }

    /// <summary>The configuration key a secret named <paramref name="name"/> fills, or null when it isn't agentd's (the prefix).</summary>
    public static string? KeyFor(string name, string? prefix)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!string.IsNullOrEmpty(prefix))
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            name = name[prefix.Length..];
        }

        return name.Length == 0 ? null : SecretStore.ConfigKey(name.Replace("--", ":", StringComparison.Ordinal));
    }

    private sealed class Provider(SecretClient client, string? prefix, string vault, TextWriter warnings) : ConfigurationProvider
    {
        public override void Load()
        {
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var properties in client.GetPropertiesOfSecrets())
                {
                    if (properties.Enabled == false || KeyFor(properties.Name, prefix) is not { } key)
                    {
                        continue;
                    }

                    data[key] = client.GetSecret(properties.Name).Value.Value;
                }
            }
#pragma warning disable CA1031 // a vault that can't be read must not stop `agentd doctor` or `agentd secrets`
            catch (Exception ex)
#pragma warning restore CA1031
            {
                warnings.WriteLine($"warning: Azure Key Vault {vault} couldn't be read ({ex.GetType().Name}: {FirstLine(ex.Message)}); starting without its secrets. Check the sign-in (az login, a managed identity or AZURE_CLIENT_ID/AZURE_CLIENT_SECRET/AZURE_TENANT_ID) and that it may list and get secrets.");
                data.Clear();
            }

            Data = data;
        }

        private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();
    }
}
