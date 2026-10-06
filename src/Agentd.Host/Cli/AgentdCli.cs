using System.CommandLine;
using Agentd.Host.Cli.Commands;

namespace Agentd.Host.Cli;

/// <summary>The <c>agentd</c> command line: the daemon plus operator verbs (docs/architect/deployment.md §1).</summary>
internal static class AgentdCli
{
    /// <summary>Starts the daemon (the default with no arguments) or runs a verb.</summary>
    public delegate Task<int> Daemon(string[] args, CancellationToken cancellationToken);

    public static Task<int> InvokeAsync(string[] args) =>
        InvokeAsync(args, Console.Out, Console.Error, CliHost.Build, DaemonHost.RunAsync, CancellationToken.None);

    public static async Task<int> InvokeAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<IServiceProvider> services,
        Daemon daemon,
        CancellationToken cancellationToken,
        Func<ConfigHome>? home = null,
        Func<string, string?>? readSecret = null,
        RunProcess? run = null,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(daemon);
        if (IsDaemonDefault(args))
        {
            return await daemon(args, cancellationToken).ConfigureAwait(false);
        }

        var context = new CliContext(output, error, services, home, readSecret, run, environment);
        await using (context.ConfigureAwait(false))
        {
            var parse = Build(context, daemon).Parse(args);
            if (parse.Errors.Count > 0)
            {
                foreach (var parseError in parse.Errors)
                {
                    await error.WriteLineAsync(parseError.Message).ConfigureAwait(false);
                }

                await error.WriteLineAsync("Run 'agentd --help' for usage.").ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            try
            {
                return await parse.InvokeAsync(
                    new InvocationConfiguration
                    {
                        Output = output,
                        Error = error,
                        // The daemon handles Ctrl+C / SIGTERM itself (graceful shutdown); verbs just stop.
                        ProcessTerminationTimeout = null,
                        EnableDefaultExceptionHandler = false,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or HostAbortedException))
            {
                // Unexpected failures (database down, remote unreachable): one line, not a stack trace.
                await error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
                return ExitCodes.Error;
            }
        }
    }

    /// <summary>
    /// No verb: run the daemon. Host options without a verb (e.g. <c>--urls …</c>, or the
    /// <c>--applicationName</c> that WebApplicationFactory passes) also go to the daemon.
    /// </summary>
    internal static bool IsDaemonDefault(string[] args) =>
        args.Length == 0 || (args[0].StartsWith("--", StringComparison.Ordinal) && args[0] is not ("--help" or "--version"));

    public static RootCommand Build(CliContext context, Daemon daemon)
    {
        var root = new RootCommand("agentd: runs coding agents for Azure DevOps work items. With no arguments, runs the daemon.");
        root.Subcommands.Add(DaemonCommand.Create(context, daemon));
        root.Subcommands.Add(StatusCommand.Create(context));
        root.Subcommands.Add(RunCommand.Create(context));
        root.Subcommands.Add(RepoCommand.Create(context));
        root.Subcommands.Add(DbCommand.Create(context));
        root.Subcommands.Add(DoctorCommand.Create(context));
        root.Subcommands.Add(SecretsCommand.Create(context));
        root.Subcommands.Add(UpdateCommand.Update(context));
        root.Subcommands.Add(UpdateCommand.Version(context));
        return root;
    }
}
