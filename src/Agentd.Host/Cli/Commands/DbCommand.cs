using System.CommandLine;
using Agentd.Migrator;

namespace Agentd.Host.Cli.Commands;

internal static class DbCommand
{
    public static Command Create(CliContext context)
    {
        var migrate = new Command("migrate", "Apply database migrations and routines (the Migrator, in-process).");
        migrate.SetAction(async (_, ct) =>
        {
            var connectionString = context.Services.GetRequiredService<IConfiguration>().GetConnectionString("agentd");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                await context.Error.WriteLineAsync("No database configured: set ConnectionStrings:agentd or Agentd:Database:ConnectionString.").ConfigureAwait(false);
                return ExitCodes.Error;
            }

            var result = await new SchemaMigrator(connectionString, context.Services.GetRequiredService<ILoggerFactory>())
                .MigrateAsync(ct).ConfigureAwait(false);
            await context.Out.WriteLineAsync(result.AppliedVersions.Count == 0
                ? "Database is up to date."
                : $"Applied {result.AppliedVersions.Count} migration(s): {string.Join(", ", result.AppliedVersions)}.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });

        return new Command("db", "Database maintenance.") { migrate };
    }
}
