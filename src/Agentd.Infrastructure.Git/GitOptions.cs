namespace Agentd.Infrastructure.Git;

/// <summary>Configuration section <c>Agentd:Git</c>. Paths default to the agentd config home (~/.agentd).</summary>
public sealed class GitOptions
{
    public const string Section = "Agentd:Git";

    /// <summary>Managed bare clones: {RepositoriesRoot}/{org}/{project}/{repo}.git</summary>
    public string RepositoriesRoot { get; set; } = "~/.agentd/repos";

    /// <summary>Per-job worktrees: {WorktreeRoot}/{repository}/wi-{id}</summary>
    public string WorktreeRoot { get; set; } = "~/.agentd/worktrees";

    /// <summary>agentd's own SSH key; used for every git call when the file exists (see deployment.md §5).</summary>
    public string SshKeyPath { get; set; } = "~/.agentd/ssh/id_ed25519";

    public string KnownHostsPath { get; set; } = "~/.agentd/ssh/known_hosts";

    /// <summary>Keep worktrees after a job ends (debugging).</summary>
    public bool KeepWorktrees { get; set; }

    /// <summary>Commit identity configured in managed clones (agents commit as this).</summary>
    public string CommitName { get; set; } = "agentd";

    public string CommitEmail { get; set; } = "agentd@users.noreply.local";

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public static string Expand(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/', '\\'))
            : path;
}
