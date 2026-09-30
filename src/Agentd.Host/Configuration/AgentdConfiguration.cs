using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;

namespace Agentd.Host.Configuration;

internal static class AgentdConfiguration
{
    public const string EnvironmentPrefix = "AGENTD_";

    /// <summary>
    /// Layers the config home on top of the host's configuration (lowest → highest): home defaults,
    /// appsettings (development only), <c>config/agentd.json</c>, <c>AGENTD_*</c> variables, command line.
    /// Keys in agentd.json and AGENTD_* follow the schema of the <c>Agentd</c> section.
    /// </summary>
    public static void AddConfigHome(this IConfigurationManager configuration, ConfigHome home, string[] args)
    {
        configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = home.Defaults() });

        var json = new JsonConfigurationSource
        {
            FileProvider = new PhysicalFileProvider(Path.GetDirectoryName(home.ConfigFile)!),
            Path = Path.GetFileName(home.ConfigFile),
            Optional = true,
            ReloadOnChange = false,
        };
        configuration.Add(new PrefixedConfigurationSource(json, "Agentd"));
        configuration.Add(new PrefixedConfigurationSource(new EnvironmentVariablesConfigurationSource { Prefix = EnvironmentPrefix }, "Agentd"));
        configuration.AddCommandLine(args);

        // A connection string may also live in the Agentd section (agentd.json / AGENTD_Database__ConnectionString).
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("agentd"))
            && configuration["Agentd:Database:ConnectionString"] is { Length: > 0 } cs)
        {
            configuration["ConnectionStrings:agentd"] = cs;
        }
    }
}
