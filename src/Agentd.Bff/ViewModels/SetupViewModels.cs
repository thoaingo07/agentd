namespace Agentd.Bff.ViewModels;

/// <summary>The setup session (the one-time link's cookie): when it ends unless used again (it slides).</summary>
public sealed record SetupSessionVm(DateTimeOffset? ExpiresAt);

/// <summary>
/// A secret's status. Secrets are write-only: the value never leaves the server. <c>overriddenBy</c> names what supplies it
/// over the secret store (e.g. "Key Vault (my-vault.vault.azure.net)"): change it there, since saving here changes nothing.
/// </summary>
public sealed record SecretStatusVm(bool Set, DateTimeOffset? UpdatedAt, string? UpdatedBy, string? OverriddenBy = null)
{
    public static SecretStatusVm From(Application.Setup.SecretStatus s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Set, s.UpdatedAt, s.UpdatedBy, s.OverriddenBy);
    }
}

/// <summary>
/// Where artifacts are stored: the S3 API URL (none: the database) and the public domain links use; the keys as statuses.
/// <c>urlOverriddenBy</c> / <c>publicUrlOverriddenBy</c> name what supplies a value over agentd.json.
/// </summary>
public sealed record ArtifactStorageStepVm(string? Url, string? PublicUrl, string? UrlOverriddenBy, string? PublicUrlOverriddenBy, SecretStatusVm AccessKeyId, SecretStatusVm SecretAccessKey)
{
    public static ArtifactStorageStepVm From(Application.Setup.ArtifactStorageStep s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Url, s.PublicUrl, s.UrlOverriddenBy, s.PublicUrlOverriddenBy, SecretStatusVm.From(s.AccessKeyId), SecretStatusVm.From(s.SecretAccessKey));
    }
}

/// <summary>Settings → Folders: the worktree root and the clones' folder as written (<c>~</c> not expanded), and what overrides each.</summary>
public sealed record FoldersStepVm(string? Worktrees, string? Repositories, string? WorktreesOverriddenBy, string? RepositoriesOverriddenBy)
{
    public static FoldersStepVm From(Application.Setup.FoldersStep s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Worktrees, s.Repositories, s.WorktreesOverriddenBy, s.RepositoriesOverriddenBy);
    }
}

public sealed record DatabaseStepVm(SecretStatusVm ConnectionString);

/// <summary><c>Auth</c> is <c>Pat</c>, <c>AzCli</c> or <c>ServicePrincipal</c> (tenant and client ids; the client secret as a status only).</summary>
public sealed record AzureDevOpsStepVm(string? Organization, string? Project, string Auth, SecretStatusVm Pat, string? TenantId, string? ClientId, SecretStatusVm ClientSecret);

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

/// <summary>A repository in <c>agentd.json</c>; the daemon registers and clones it at start.</summary>
public sealed record RepositoryEntryVm(string Url, string? Name, string? BaseBranch, string? MatchTag, IReadOnlyList<string> MatchAreaPaths)
{
    public static RepositoryEntryVm From(Application.Setup.RepositoryEntry r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(r.Url, r.Name, r.BaseBranch, r.MatchTag, r.MatchAreaPaths);
    }
}

/// <summary>One review line: a step, whether finishing needs it, and its check.</summary>
public sealed record ReviewItemVm(string Step, string Title, bool Required, StepCheckVm Check)
{
    public static ReviewItemVm From(Application.Setup.ReviewItem i)
    {
        ArgumentNullException.ThrowIfNull(i);
        return new(i.Step, i.Title, i.Required, new StepCheckVm(i.Check.Ok, i.Check.Message, i.Check.Fix));
    }
}

/// <summary>Setup is complete; the daemon uses the new settings after a restart.</summary>
public sealed record FinishVm(DateTimeOffset CompletedAt);

/// <summary>Another provider: endpoint and models; the API key as a status only.</summary>
public sealed record ProfileVm(string Name, string? BaseUrl, string? Model, string? SmallModel, SecretStatusVm ApiKey);

/// <summary>One step's model, effort and provider (empty: the defaults).</summary>
public sealed record StepModelVm(string Step, string? Model, string? Effort, string? Profile);

public sealed record ModelsStepVm(IReadOnlyList<ProfileVm> Profiles, IReadOnlyList<StepModelVm> Steps)
{
    public static ModelsStepVm From(Application.Setup.ModelsStep m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new(
            [.. m.Profiles.Select(p => new ProfileVm(p.Name, p.BaseUrl, p.Model, p.SmallModel, SecretStatusVm.From(p.ApiKey)))],
            [.. m.Steps.Select(s => new StepModelVm(s.Step, s.Model, s.Effort, s.Profile))]);
    }
}
