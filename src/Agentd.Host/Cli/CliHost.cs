namespace Agentd.Host.Cli;

/// <summary>The services for CLI verbs: the daemon's configuration and use cases, without the web host or workers.</summary>
internal static class CliHost
{
    public static IServiceProvider Build()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            // `dotnet run` launch profiles set ASPNETCORE_ENVIRONMENT; honor it like the daemon does.
            EnvironmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? Environments.Production,
        });
        builder.AddAgentdCore([]);
        // Verbs print their own results; only warnings and errors are logged (appsettings levels target the daemon).
        builder.Configuration.AddInMemoryCollection([new("Logging:LogLevel:Default", "Warning")]);
        return builder.Build().Services;
    }
}
