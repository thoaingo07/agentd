using System.Collections.Concurrent;
using System.Globalization;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Git;

/// <summary>
/// One managed bare clone per repository and one worktree per job (branch <c>ai/&lt;id&gt;-&lt;slug&gt;</c> from
/// <c>origin/&lt;base&gt;</c>). Operations on the same repository are serialized; different repositories run in parallel.
/// </summary>
public sealed partial class GitWorktreeManager(GitCli git, IOptions<GitOptions> options) : IWorktreeManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task EnsureCloneAsync(Repository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        await WithRepoLockAsync(repository, () => EnsureCloneCoreAsync(repository, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorktreePath> CreateAsync(Repository repository, WorkItemId workItem, BranchName branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return await WithRepoLockAsync(repository, async () =>
        {
            var clone = await EnsureCloneCoreAsync(repository, cancellationToken).ConfigureAwait(false);
            var path = WorktreePathFor(repository, workItem);

            if (Directory.Exists(path))
            {
                var current = await git.RunAsync(path, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken, throwOnError: false).ConfigureAwait(false);
                if (current.ExitCode == 0 && current.StandardOutput == branch.Value)
                {
                    return new WorktreePath(path);   // retry/resume: reuse the existing worktree
                }

                throw new GitException($"Worktree path {path} already exists for another branch ({current.StandardOutput}).");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var branchExists = (await git.RunAsync(clone, ["show-ref", "--verify", "--quiet", "refs/heads/" + branch.Value], cancellationToken, throwOnError: false).ConfigureAwait(false)).ExitCode == 0;
            string[] add = branchExists
                ? ["worktree", "add", path, branch.Value]
                : ["worktree", "add", "-b", branch.Value, path, "origin/" + repository.BaseBranch];
            await git.RunAsync(clone, add, cancellationToken).ConfigureAwait(false);
            return new WorktreePath(path);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorktreePath> RecreateAsync(Repository repository, WorkItemId workItem, BranchName branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return await WithRepoLockAsync(repository, async () =>
        {
            var clone = await EnsureCloneCoreAsync(repository, cancellationToken).ConfigureAwait(false);
            var path = WorktreePathFor(repository, workItem);
            if (Directory.Exists(path))
            {
                await git.RunAsync(clone, ["worktree", "remove", "--force", path], cancellationToken, throwOnError: false).ConfigureAwait(false);
            }

            await git.RunAsync(clone, ["worktree", "prune"], cancellationToken, throwOnError: false).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var branchExists = (await git.RunAsync(clone, ["show-ref", "--verify", "--quiet", "refs/heads/" + branch.Value], cancellationToken, throwOnError: false).ConfigureAwait(false)).ExitCode == 0;
            string[] add = branchExists
                ? ["worktree", "add", path, branch.Value]
                : ["worktree", "add", "-b", branch.Value, path, "origin/" + repository.BaseBranch];
            await git.RunAsync(clone, add, cancellationToken).ConfigureAwait(false);
            return new WorktreePath(path);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasCommitsAheadAsync(Repository repository, WorktreePath worktree, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        // Fresh base first: after a merge, the work is in origin/<base>, so a stale copy would make it look new.
        await git.RunAsync(worktree.Value, ["fetch", "origin", repository.BaseBranch], cancellationToken, throwOnError: false).ConfigureAwait(false);
        var count = await git.RunAsync(worktree.Value, ["rev-list", "--count", $"origin/{repository.BaseBranch}..HEAD"], cancellationToken).ConfigureAwait(false);
        return int.Parse(count.StandardOutput, CultureInfo.InvariantCulture) > 0;
    }

    public async Task PushAsync(WorktreePath worktree, BranchName branch, CancellationToken cancellationToken) =>
        await git.RunAsync(worktree.Value, BuildPushArgs(branch), cancellationToken).ConfigureAwait(false);

    public async Task RemoveAsync(Repository repository, WorktreePath worktree, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (options.Value.KeepWorktrees)
        {
            return;
        }

        await WithRepoLockAsync(repository, async () =>
        {
            // --force: the worktree belongs to agentd; the branch (and its pushed commits) is kept.
            await git.RunAsync(ClonePathFor(repository), ["worktree", "remove", "--force", worktree.Value], cancellationToken, throwOnError: false).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task PruneAsync(Repository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var clone = ClonePathFor(repository);
        if (Directory.Exists(clone))
        {
            await git.RunAsync(clone, ["worktree", "prune"], cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<BranchDiff?> DiffAsync(Repository repository, BranchName branch, WorktreePath? worktree, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var baseRef = "origin/" + repository.BaseBranch;
        string cwd;
        string[] range;
        if (worktree is { } live && Directory.Exists(live.Value))
        {
            // Against the merge base, so uncommitted edits show and later base commits don't.
            cwd = live.Value;
            var mergeBase = await git.RunAsync(cwd, ["merge-base", baseRef, "HEAD"], cancellationToken, throwOnError: false).ConfigureAwait(false);
            if (mergeBase.ExitCode != 0)
            {
                return null;
            }

            range = [mergeBase.StandardOutput];
        }
        else
        {
            cwd = ClonePathFor(repository);
            var exists = Directory.Exists(cwd)
                && (await git.RunAsync(cwd, ["show-ref", "--verify", "--quiet", "refs/heads/" + branch.Value], cancellationToken, throwOnError: false).ConfigureAwait(false)).ExitCode == 0;
            if (!exists)
            {
                return null;
            }

            range = [$"{baseRef}...{branch.Value}"];
        }

        var names = await git.RunAsync(cwd, ["diff", "--name-only", .. range], cancellationToken).ConfigureAwait(false);
        var files = names.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var stat = await git.RunAsync(cwd, ["diff", "--numstat", .. range], cancellationToken).ConfigureAwait(false);
        if (EstimateBytes(stat.StandardOutput) > maxBytes)
        {
            return new BranchDiff(baseRef, branch.Value, files, null, Truncated: true);
        }

        var diff = await git.RunAsync(cwd, ["diff", "--no-color", "--no-ext-diff", .. range], cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetByteCount(diff.StandardOutput) > maxBytes
            ? new BranchDiff(baseRef, branch.Value, files, null, Truncated: true)
            : new BranchDiff(baseRef, branch.Value, files, diff.StandardOutput.Length == 0 ? string.Empty : diff.StandardOutput + "\n", Truncated: false);
    }

    /// <summary>A lower bound on the diff size from <c>--numstat</c> (assume 10 bytes a changed line), to skip huge diffs early.</summary>
    internal static long EstimateBytes(string numstat) =>
        numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Sum(parts => (long.TryParse(parts[0], CultureInfo.InvariantCulture, out var added) ? added : 0)
                + (parts.Length > 1 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var removed) ? removed : 0)) * 10;

    public async Task<string> CheckoutDetachedAsync(Repository repository, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return await WithRepoLockAsync(repository, async () =>
        {
            var clone = await EnsureCloneCoreAsync(repository, cancellationToken).ConfigureAwait(false);
            var path = Path.Combine(GitOptions.Expand(options.Value.WorktreeRoot), Safe(repository.Name.Value), Safe(name));
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await git.RunAsync(clone, ["worktree", "add", "--detach", path, "origin/" + repository.BaseBranch], cancellationToken).ConfigureAwait(false);
            }

            return path;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> CheckoutCommitAsync(Repository repository, string name, string commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!CommitId().IsMatch(commit ?? string.Empty))
        {
            throw new ArgumentException($"'{commit}' isn't a commit id.", nameof(commit));
        }

        return await WithRepoLockAsync(repository, async () =>
        {
            var clone = await EnsureCloneCoreAsync(repository, cancellationToken).ConfigureAwait(false);
            await git.RunAsync(clone, ["fetch", "--prune", "origin"], cancellationToken).ConfigureAwait(false);
            var path = Path.Combine(GitOptions.Expand(options.Value.WorktreeRoot), Safe(repository.Name.Value), Safe(name));
            if (Directory.Exists(path))
            {
                await git.RunAsync(path, ["checkout", "--detach", "--force", commit!], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await git.RunAsync(clone, ["worktree", "add", "--detach", path, commit!], cancellationToken).ConfigureAwait(false);
            }

            return path;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Push arguments. There is deliberately no way to request a force push.</summary>
    internal static IReadOnlyList<string> BuildPushArgs(BranchName branch) => ["push", "--set-upstream", "origin", branch.Value];

    internal string ClonePathFor(Repository repository) =>
        Path.Combine(
            GitOptions.Expand(options.Value.RepositoriesRoot),
            Safe(repository.AzureDevOps.Organization),
            Safe(repository.AzureDevOps.Project),
            Safe(repository.AzureDevOps.Name) + ".git");

    internal string WorktreePathFor(Repository repository, WorkItemId workItem) =>
        Path.Combine(GitOptions.Expand(options.Value.WorktreeRoot), Safe(repository.Name.Value), $"wi-{workItem}");

    private async Task<string> EnsureCloneCoreAsync(Repository repository, CancellationToken ct)
    {
        var clone = ClonePathFor(repository);
        if (!Directory.Exists(Path.Combine(clone, "objects")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(clone)!);
            await git.RunAsync(null, ["clone", "--bare", "--", repository.RemoteUrl, clone], ct).ConfigureAwait(false);
            // Remote-tracking refs (origin/*) so worktrees can start from origin/<base>.
            await git.RunAsync(clone, ["config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*"], ct).ConfigureAwait(false);
            await git.RunAsync(clone, ["config", "user.name", options.Value.CommitName], ct).ConfigureAwait(false);
            await git.RunAsync(clone, ["config", "user.email", options.Value.CommitEmail], ct).ConfigureAwait(false);
        }

        await git.RunAsync(clone, ["fetch", "--prune", "origin"], ct).ConfigureAwait(false);
        return clone;
    }

    private async Task<T> WithRepoLockAsync<T>(Repository repository, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(ClonePathFor(repository), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string Safe(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(segment.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim('.', ' ');
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    /// <summary>A full or abbreviated commit id: nothing else ever reaches git as a revision here.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("^[0-9a-f]{7,40}$")]
    private static partial System.Text.RegularExpressions.Regex CommitId();
}
