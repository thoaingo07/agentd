using System.Diagnostics;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;
using Agentd.Infrastructure.Git;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Tests.Git;

/// <summary>A throwaway "remote" (bare repo with a develop branch) plus agentd roots, all under a temp dir.</summary>
internal sealed class GitSandbox : IDisposable
{
    public GitSandbox()
    {
        Root = Directory.CreateTempSubdirectory("agentd-git-").FullName;
        RemotePath = Path.Combine(Root, "remote.git");
        Run(Root, "init", "--bare", "--initial-branch=develop", RemotePath);

        var seed = Path.Combine(Root, "seed");
        Run(Root, "clone", RemotePath, seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "hello\n");
        Run(seed, "add", ".");
        Run(seed, "-c", "user.name=seed", "-c", "user.email=seed@x", "commit", "-m", "initial");
        Run(seed, "push", "origin", "HEAD:develop");

        Options = Microsoft.Extensions.Options.Options.Create(new GitOptions
        {
            RepositoriesRoot = Path.Combine(Root, "repos"),
            WorktreeRoot = Path.Combine(Root, "worktrees"),
            SshKeyPath = Path.Combine(Root, "no-such-key"),
        });
        Git = new GitCli(Options);
        Manager = new GitWorktreeManager(Git, Options);
        Repository = new Repository(RepositoryName.From("sysmin"), RemotePath, new AzureDevOpsRepo("ermsystem", "Portal", "sysmin"), "develop", "repo:sysmin", []);
    }

    public string Root { get; }

    public string RemotePath { get; }

    public IOptions<GitOptions> Options { get; }

    public GitCli Git { get; }

    public GitWorktreeManager Manager { get; }

    public Repository Repository { get; }

    public static string Run(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    public static void Commit(WorktreePath worktree, string file, string content)
    {
        File.WriteAllText(Path.Combine(worktree.Value, file), content);
        Run(worktree.Value, "add", file);
        Run(worktree.Value, "commit", "-m", $"change {file}");   // identity comes from the managed clone's config
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp dir.
        }
    }
}
