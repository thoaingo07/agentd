using System.CommandLine;
using System.Globalization;
using Agentd.Host.Configuration;

namespace Agentd.Host.Cli.Commands;

/// <summary><c>agentd secrets set|list|remove</c>: the encrypted secret store (docs/architect/deployment.md §4).</summary>
internal static class SecretsCommand
{
    public static Command Create(CliContext context) => new("secrets", "Encrypted secrets (PATs, bot tokens, Claude tokens, the database connection).")
    {
        Set(context),
        List(context),
        Remove(context),
    };

    private static Command Set(CliContext context)
    {
        var key = new Argument<string>("key") { Description = "e.g. AzureDevOps:Pat, Messaging:Providers:discord:BotToken, ConnectionStrings:agentd" };
        // A value argument would land in the shell history and `ps`; it's read from stdin or a no-echo prompt instead.
        var command = new Command("set", "Store a secret. The value is read from stdin (piped) or a hidden prompt, never from the command line.") { key };
        command.SetAction(async (parse, _) =>
        {
            var name = parse.GetValue(key)!;
            if (!SecretStore.IsValidKey(name))
            {
                await context.Error.WriteLineAsync($"Invalid secret name '{name}': use letters, digits and ':', '_', '-', '.'.").ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            if (parse.UnmatchedTokens.Count > 0)
            {
                await context.Error.WriteLineAsync("Don't pass the value as an argument (it would be in your shell history). Pipe it in or type it at the prompt.").ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            var value = context.ReadSecret($"Value for {name} (hidden): ");
            if (string.IsNullOrEmpty(value))
            {
                await context.Error.WriteLineAsync("No value given; nothing stored.").ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            new SecretStore(context.Home).Set(name, value, Environment.UserName);
            await context.Out.WriteLineAsync($"Stored {name} (encrypted). Restart the daemon to use it.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        command.TreatUnmatchedTokensAsErrors = false;
        return command;
    }

    private static Command List(CliContext context)
    {
        var command = new Command("list", "List secret names and when they were set (never the values).");
        command.SetAction(async (_, _) =>
        {
            var store = new SecretStore(context.Home);
            var secrets = store.List();
            if (secrets.Count == 0)
            {
                await context.Out.WriteLineAsync("No secrets stored.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            var unreadable = store.Load().Unreadable.ToHashSet(StringComparer.Ordinal);
            await context.Out.WriteAsync(TextTable.Render(
                [["NAME", "UPDATED", "BY", "STATUS"],
                 .. secrets.Select(s => new[] { s.Key, s.UpdatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), s.UpdatedBy,
                    unreadable.Contains(s.Key) ? "can't decrypt: set it again" : "ok" })])).ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Remove(CliContext context)
    {
        var key = new Argument<string>("key");
        var command = new Command("remove", "Delete a secret.") { key };
        command.SetAction(async (parse, _) =>
        {
            var name = parse.GetValue(key)!;
            if (!new SecretStore(context.Home).Remove(name))
            {
                await context.Error.WriteLineAsync($"No secret named {name}.").ConfigureAwait(false);
                return ExitCodes.Error;
            }

            await context.Out.WriteLineAsync($"Removed {name}.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }
}
