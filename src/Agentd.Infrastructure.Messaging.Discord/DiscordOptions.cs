using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Messaging.Discord;

/// <summary>Configuration section <c>Agentd:Messaging:Providers:Discord</c>. The bot token comes from secrets only.</summary>
public sealed class DiscordOptions
{
    public const string Section = "Agentd:Messaging:Providers:Discord";

    public bool Enabled { get; set; }

    /// <summary>Secret: <c>Agentd__Messaging__Providers__Discord__BotToken</c> or user-secrets. Never in appsettings.</summary>
    public string? BotToken { get; set; }

    /// <summary>The server (guild) id.</summary>
    public string GuildId { get; set; } = "";

    /// <summary>The parent text channel; agentd creates one thread per job in it.</summary>
    public string ChannelId { get; set; } = "";

    public Uri ApiBaseUrl { get; set; } = new("https://discord.com/api/v10/");

    /// <summary>How often open job threads and the parent channel are polled for new messages.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Prefix for typed commands, e.g. <c>!status</c>.</summary>
    public string CommandPrefix { get; set; } = "!";
}

/// <summary>Fails startup when Discord is enabled without what it needs.</summary>
public sealed class DiscordOptionsValidator : IValidateOptions<DiscordOptions>
{
    public ValidateOptionsResult Validate(string? name, DiscordOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.BotToken))
        {
            errors.Add("Discord is enabled but has no bot token: set the secret Agentd:Messaging:Providers:Discord:BotToken (user-secrets or the Agentd__Messaging__Providers__Discord__BotToken variable).");
        }

        if (!ulong.TryParse(options.GuildId, out _))
        {
            errors.Add("Agentd:Messaging:Providers:Discord:GuildId must be the numeric server id.");
        }

        if (!ulong.TryParse(options.ChannelId, out _))
        {
            errors.Add("Agentd:Messaging:Providers:Discord:ChannelId must be the numeric channel id.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
