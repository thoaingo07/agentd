namespace Agentd.Bff.ViewModels;

/// <summary>The setup session (the one-time link's cookie): when it ends unless used again (it slides).</summary>
public sealed record SetupSessionVm(DateTimeOffset? ExpiresAt);

/// <summary>A secret's status. Secrets are write-only: the value never leaves the server.</summary>
public sealed record SecretStatusVm(bool Set, DateTimeOffset? UpdatedAt, string? UpdatedBy)
{
    public static SecretStatusVm From(Application.Setup.SecretStatus s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Set, s.UpdatedAt, s.UpdatedBy);
    }
}

public sealed record DatabaseStepVm(SecretStatusVm ConnectionString);

/// <summary><c>Auth</c> is <c>Pat</c> or <c>AzCli</c>.</summary>
public sealed record AzureDevOpsStepVm(string? Organization, string? Project, string Auth, SecretStatusVm Pat);

/// <summary>A step's "Test" result; <see cref="Fix"/> says what to do when it failed.</summary>
public sealed record StepCheckVm(bool Ok, string Message, string? Fix);

/// <summary>Saved. The daemon reads these settings at start, so they apply after a restart.</summary>
public sealed record SaveResultVm(bool RestartRequired);

/// <summary>agentd's SSH key for git: the public key to add in Azure DevOps or GitHub, and its fingerprint. Never the private key.</summary>
public sealed record GitKeyStepVm(bool Exists, string? PublicKey, string? Fingerprint, string? Path)
{
    public static GitKeyStepVm From(Application.Setup.GitKeyStep s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Exists, s.PublicKey, s.Fingerprint, s.Path);
    }
}

/// <summary>Claude Code on this server: installed, its version, and whether a subscription is logged in there.</summary>
public sealed record ClaudeLoginVm(bool Installed, string? Version, bool LoggedIn, string? Method, string? Plan);

/// <summary>The Claude step: the stored <c>claude setup-token</c> token (status only) and the server's own login.</summary>
public sealed record ClaudeStepVm(SecretStatusVm Token, ClaudeLoginVm Server)
{
    public static ClaudeStepVm From(Application.Setup.ClaudeStep s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(SecretStatusVm.From(s.Token), new ClaudeLoginVm(s.Server.Installed, s.Server.Version, s.Server.LoggedIn, s.Server.Method, s.Server.Plan));
    }
}

public sealed record ChatUserVm(string Name, string DiscordId);

/// <summary>The chat step (Discord): the server and channel IDs, the bot token's status, and who agentd answers there.</summary>
public sealed record ChatStepVm(bool Enabled, string? GuildId, string? ChannelId, SecretStatusVm BotToken, IReadOnlyList<ChatUserVm> Users)
{
    public static ChatStepVm From(Application.Setup.ChatStep s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Enabled, s.GuildId, s.ChannelId, SecretStatusVm.From(s.BotToken), [.. s.Users.Select(u => new ChatUserVm(u.Name, u.DiscordId))]);
    }
}
