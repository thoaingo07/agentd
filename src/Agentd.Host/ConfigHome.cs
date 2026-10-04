namespace Agentd.Host;

/// <summary>
/// agentd's config home: <c>AGENTD_HOME</c> or <c>~/.agentd</c>, holding config, managed clones,
/// worktrees, transcripts, Claude logins and runtime files (see docs/architect/deployment.md §2).
/// </summary>
internal sealed class ConfigHome(string root)
{
    public const string Variable = "AGENTD_HOME";

    private static readonly string[] s_folders = ["config", "repos", "worktrees", "logs", "claude", "run", "keys"];

    public string Root { get; } = Path.GetFullPath(root);

    public string ConfigFile => Path.Combine(Root, "config", "agentd.json");

    public string Repos => Path.Combine(Root, "repos");

    public string Worktrees => Path.Combine(Root, "worktrees");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>ASP.NET Core Data Protection keys (antiforgery and, later, auth cookies), so they survive restarts.</summary>
    public string Keys => Path.Combine(Root, "keys");

    public static ConfigHome Resolve(Func<string, string?>? environment = null)
    {
        var configured = (environment ?? Environment.GetEnvironmentVariable)(Variable);
        return new ConfigHome(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentd")
            : configured);
    }

    /// <summary>Creates the home and its folders, readable only by the current user (0700).</summary>
    public ConfigHome EnsureCreated()
    {
        foreach (var dir in s_folders.Select(f => Path.Combine(Root, f)).Prepend(Root))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(dir);
            }
            else
            {
                Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        return this;
    }

    /// <summary>Lowest-priority defaults that place agentd's files inside the home.</summary>
    public IEnumerable<KeyValuePair<string, string?>> Defaults() =>
    [
        new("Agentd:Git:RepositoriesRoot", Repos),
        new("Agentd:Git:WorktreeRoot", Worktrees),
        new("Agentd:Git:SshKeyPath", Path.Combine(Root, "ssh", "id_ed25519")),
        new("Agentd:Git:KnownHostsPath", Path.Combine(Root, "ssh", "known_hosts")),
        new("Agentd:Claude:TranscriptRoot", Logs),
    ];
}
