using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;

namespace Agentd.Host.Configuration;

internal static class AgentdConfiguration
{
    public const string EnvironmentPrefix = "AGENTD_";

    /// <summary>The embedded copy of the Host's appsettings.json.</summary>
    public const string EmbeddedDefaults = "agentd.appsettings.json";

    /// <summary>
    /// Layers the config home on top of the host's configuration (lowest → highest): home defaults,
    /// appsettings (development only), <c>config/agentd.json</c>, the encrypted <c>config/secrets.json</c>,
    /// <c>AGENTD_*</c> variables, command line.
    /// Keys in agentd.json and AGENTD_* follow the schema of the <c>Agentd</c> section.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="home">agentd's home (agentd.json, secrets.json).</param>
    /// <param name="args">The command line.</param>
    /// <param name="vault">The Key Vault source to use instead of <see cref="KeyVaultConfigurationSource.From"/> (tests).</param>
    public static void AddConfigHome(this IConfigurationManager configuration, ConfigHome home, string[] args, Func<IConfiguration, IConfigurationSource?>? vault = null)
    {
        configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = home.Defaults() });
        // appsettings.json is embedded too, so the single-file agentd has its defaults with no file next to it;
        // a file on disk (development) still overrides it.
        if (typeof(AgentdConfiguration).Assembly.GetManifestResourceStream(EmbeddedDefaults) is { } defaults)
        {
            configuration.Sources.Insert(1, new JsonStreamConfigurationSource { Stream = defaults });
        }

        var json = new JsonConfigurationSource
        {
            FileProvider = new PhysicalFileProvider(Path.GetDirectoryName(home.ConfigFile)!),
            Path = Path.GetFileName(home.ConfigFile),
            Optional = true,
            ReloadOnChange = false,
        };
        configuration.Add(new PrefixedConfigurationSource(json, "Agentd"));
        configuration.Add(new SecretsConfigurationSource(home));   // already prefixed (Agentd:… or ConnectionStrings:…)

        // Azure Key Vault, when Agentd:KeyVault:Uri is set (agentd.json, AGENTD_KeyVault__Uri or the command line): after
        // secrets.json, before the environment, so a variable can still override a vault value. Its settings are read from
        // a chained view (appending sources loads only the new one; inserting would reload every source).
        var environment = new PrefixedConfigurationSource(new EnvironmentVariablesConfigurationSource { Prefix = EnvironmentPrefix }, "Agentd");
        var settings = new ConfigurationBuilder().AddConfiguration(configuration, shouldDisposeConfiguration: false).Add(environment).AddCommandLine(args).Build();
        if ((vault ?? KeyVaultConfigurationSource.From)(settings) is { } keyVault)
        {
            configuration.Add(keyVault);
        }

        configuration.Add(environment);
        configuration.AddCommandLine(args);

        // A connection string may also live in the Agentd section (agentd.json / AGENTD_Database__ConnectionString).
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("agentd"))
            && configuration["Agentd:Database:ConnectionString"] is { Length: > 0 } cs)
        {
            configuration["ConnectionStrings:agentd"] = cs;
        }
    }
}
