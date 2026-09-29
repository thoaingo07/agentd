using Agentd.Migrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Agentd.Migrator: apply schema migrations (FluentMigrator) and PL/pgSQL routines, then exit.
//   Aspire:     started automatically; the Host waits for it to complete successfully.
//   Standalone: ConnectionStrings__agentd="Host=…;Database=agentd;…" dotnet run --project src/Agentd.Migrator
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

using var host = builder.Build();
var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
var logger = loggerFactory.CreateLogger("Agentd.Migrator");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    var connectionString = builder.Configuration.GetConnectionString("agentd")
        ?? throw new InvalidOperationException("Connection string 'agentd' is not configured (ConnectionStrings__agentd).");

    await new SchemaMigrator(connectionString, loggerFactory).MigrateAsync(cancellation.Token).ConfigureAwait(false);
    return 0;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    MigratorLog.Failed(logger, ex);
    return 1;
}
finally
{
    // Flush telemetry (e.g. to the Aspire dashboard) before exiting.
    await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
}

internal static partial class MigratorLog
{
    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration failed")]
    public static partial void Failed(ILogger logger, Exception exception);
}
