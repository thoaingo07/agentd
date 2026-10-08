using Agentd.Application.Jobs;

namespace Agentd.Infrastructure.Claude;

/// <summary>
/// Claude Code against another provider's Anthropic-compatible endpoint (a <see cref="ModelProfile"/>): the variables the
/// provider's Claude Code guide asks for, set only in that turn's process. The Claude subscription token is removed, so the
/// turn can't fall back to it, and the profile has its own config and session folder.
/// </summary>
public static class ProfileEnvironment
{
    public static void Apply(IDictionary<string, string> env, string name, ModelProfile profile, ClaudeOptions options)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);
        env.Remove(options.OAuthTokenVariable);
        env.Remove("ANTHROPIC_API_KEY");
        env["ANTHROPIC_BASE_URL"] = profile.BaseUrl ?? throw new InvalidOperationException($"Profile {name} has no BaseUrl.");
        env["ANTHROPIC_AUTH_TOKEN"] = profile.ApiKey ?? throw new InvalidOperationException($"Profile {name} has no ApiKey.");
        if (profile.Model is { Length: > 0 } model)
        {
            env["ANTHROPIC_MODEL"] = model;
            env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = model;
            env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = model;
        }

        if ((profile.SmallModel is { Length: > 0 } ? profile.SmallModel : profile.Model) is { Length: > 0 } small)
        {
            env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = small;
            env["CLAUDE_CODE_SUBAGENT_MODEL"] = small;
        }

        env["CLAUDE_CONFIG_DIR"] = ConfigDir(name, profile, options);
        foreach (var (key, value) in profile.Environment)
        {
            env[key] = value;
        }
    }

    /// <summary><c>ConfigDir</c>, or <c>&lt;agentd home&gt;/claude/profiles/&lt;name&gt;</c> (next to the transcripts' folder), created 0700.</summary>
    public static string ConfigDir(string name, ModelProfile profile, ClaudeOptions options)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);
        var dir = profile.ConfigDir is { Length: > 0 } configured
            ? Paths.Expand(configured)
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Paths.Expand(options.TranscriptRoot)))!, "claude", "profiles", name);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
        }
        else
        {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dir;
    }
}
