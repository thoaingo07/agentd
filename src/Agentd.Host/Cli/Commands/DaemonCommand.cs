using System.CommandLine;

namespace Agentd.Host.Cli.Commands;

internal static class DaemonCommand
{
    public static Command Create(AgentdCli.Daemon daemon)
    {
        var run = new Command("run", "Run the daemon in the foreground: web UI, MCP endpoint, polling and agents. Extra arguments go to the web host (e.g. --urls).")
        {
            TreatUnmatchedTokensAsErrors = false,
        };
        run.SetAction((parse, ct) => daemon([.. parse.UnmatchedTokens], ct));

        return new Command("daemon", "Run the agentd daemon.") { run };
    }
}
