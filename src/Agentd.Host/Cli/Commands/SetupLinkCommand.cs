using System.CommandLine;
using Agentd.Bff.Setup;
using Agentd.Host.Configuration;
using Agentd.Host.Options;

namespace Agentd.Host.Cli.Commands;

/// <summary><c>agentd setup-link</c>: a new one-time setup link (the old one stops working). Only before setup completes.</summary>
internal static class SetupLinkCommand
{
    public static Command Create(CliContext context)
    {
        var command = new Command("setup-link", "Print a new one-time link to the setup wizard (until setup is complete; the previous link stops working).");
        command.SetAction(async (_, _) =>
        {
            var configuration = context.Services.GetService<IConfiguration>();
            if (SetupState.IsCompleteIn(context.Home, configuration))
            {
                await context.Error.WriteLineAsync("agentd is already set up, so there's no setup link. Change settings in the web UI (Settings) or with `agentd secrets`.").ConfigureAwait(false);
                return ExitCodes.Conflict;
            }

            var urls = configuration?.GetSection($"{AgentdOptions.Section}:Web").Get<WebOptions>()?.Urls ?? SetupLink.DefaultBase;
            var token = new SetupToken(context.Home.SetupTokenFile).Issue();
            var link = SetupLink.For(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), token);
            await context.Out.WriteLineAsync(link).ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Open it on this machine, or from another computer through an SSH tunnel: {SetupLink.Tunnel(link)}. It works until setup is complete.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }
}
