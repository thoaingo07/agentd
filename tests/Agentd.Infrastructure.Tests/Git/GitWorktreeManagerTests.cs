using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Infrastructure.Git;

namespace Agentd.Infrastructure.Tests.Git;

[TestClass]
public sealed class GitWorktreeManagerTests
{
    private static readonly WorkItemId s_wi = WorkItemId.From(1234);
    private static readonly BranchName s_branch = BranchName.For(s_wi, "Fix login");

    [TestMethod]
    public async Task Creates_a_bare_clone_and_a_worktree_on_a_new_branch_from_origin_base()
    {
        using var box = new GitSandbox();

        var worktree = await box.Manager.CreateAsync(box.Repository, s_wi, s_branch, default);

        Assert.IsTrue(Directory.Exists(Path.Combine(box.Manager.ClonePathFor(box.Repository), "objects")), "bare clone exists");
        Assert.AreEqual("ai/1234-fix-login", GitSandbox.Run(worktree.Value, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.AreEqual(GitSandbox.Run(box.RemotePath, "rev-parse", "develop"), GitSandbox.Run(worktree.Value, "rev-parse", "HEAD"));
        StringAssert.EndsWith(worktree.Value, Path.Combine("worktrees", "sysmin", "wi-1234"));
    }

    [TestMethod]
    public async Task Recreating_keeps_the_path_and_moves_to_a_new_branch_from_the_latest_base()
    {
        using var box = new GitSandbox();
        var first = await box.Manager.CreateAsync(box.Repository, s_wi, s_branch, default);
        GitSandbox.Commit(first, "draft.txt", "work\n");
        var knowledge = BranchName.For(s_wi, "knowledge");

        var second = await box.Manager.RecreateAsync(box.Repository, s_wi, knowledge, default);

        Assert.AreEqual(first.Value, second.Value, "same path, so the Claude session can resume");
        Assert.AreEqual("ai/1234-knowledge", GitSandbox.Run(second.Value, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.AreEqual(GitSandbox.Run(box.RemotePath, "rev-parse", "develop"), GitSandbox.Run(second.Value, "rev-parse", "HEAD"));
        Assert.IsFalse(File.Exists(Path.Combine(second.Value, "draft.txt")), "starts clean from the base branch");
    }

    [TestMethod]
    public async Task Creating_again_reuses_the_existing_worktree()
    {
        using var box = new GitSandbox();
        var first = await box.Manager.CreateAsync(box.Repository, s_wi, s_branch, default);

        var second = await box.Manager.CreateAsync(box.Repository, s_wi, s_branch, default);

        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public async Task Commits_ahead_is_false_until_the_agent_commits_then_push_lands_on_the_remote()
    {
        using var box = new GitSandbox();
        var worktree = await box.Manager.CreateAsync(box.Repository, s_wi, s_branch, default);

        Assert.IsFalse(await box.Manager.HasCommitsAheadAsync(box.Repository, worktree, default));
        GitSandbox.Commit(worktree, "fix.txt", "fixed\n");
        Assert.IsTrue(await box.Manager.HasCommitsAheadAsync(box.Repository, worktree, default));

        await box.Manager.PushAsync(worktree, s_branch, default);

        Assert.AreEqual(GitSandbox.Run(worktree.Value, "rev-parse", "HEAD"), GitSandbox.Run(box.RemotePath, "rev-parse", s_branch.Value));
        Assert.AreEqual("agentd", GitSandbox.Run(box.RemotePath, "log", "-1", "--format=%an", s_branch.Value), "commits use the managed clone's identity");
    }

    [TestMethod]
    public async Task A_retry_after_removal_checks_out_the_existing_branch()
    {
        using var box = new GitSandbox();
        var worktree = await box.Manager.CreateAsync(box.Repository, s_wi, s_branch, default);
        GitSandbox.Commit(worktree, "work.txt", "partial\n");
        var head = GitSandbox.Run(worktree.Value, "rev-parse", "HEAD");
        await box.Manager.RemoveAsync(box.Repository, worktree, default);

        var again = await box.Manager.CreateAsync(box.Repository, s_wi, s_branch, default);

        Assert.AreEqual(head, GitSandbox.Run(again.Value, "rev-parse", "HEAD"), "the branch and its commits survive");
    }

    [TestMethod]
    public async Task Remove_and_prune_leave_no_registered_worktree()
    {
        using var box = new GitSandbox();
        var worktree = await box.Manager.CreateAsync(box.Repository, s_wi, s_branch, default);

        await box.Manager.RemoveAsync(box.Repository, worktree, default);
        await box.Manager.PruneAsync(box.Repository, default);

        Assert.IsFalse(Directory.Exists(worktree.Value));
        var list = GitSandbox.Run(box.Manager.ClonePathFor(box.Repository), "worktree", "list");
        Assert.DoesNotContain("wi-1234", list);
    }

    [TestMethod]
    public async Task Parallel_jobs_on_the_same_repository_get_separate_worktrees()
    {
        using var box = new GitSandbox();

        var results = await Task.WhenAll(Enumerable.Range(1, 5).Select(i =>
            box.Manager.CreateAsync(box.Repository, WorkItemId.From(i), BranchName.For(WorkItemId.From(i), $"task {i}"), default)));

        Assert.HasCount(5, results.Select(r => r.Value).Distinct());
        foreach (var (worktree, i) in results.Select((w, i) => (w, i + 1)))
        {
            Assert.AreEqual($"ai/{i}-task-{i}", GitSandbox.Run(worktree.Value, "rev-parse", "--abbrev-ref", "HEAD"));
        }
    }

    [TestMethod]
    public void Push_arguments_never_contain_force() =>
        Assert.IsFalse(GitWorktreeManager.BuildPushArgs(s_branch).Any(a => a.Contains("force", StringComparison.OrdinalIgnoreCase) || a is "-f" || a.StartsWith('+')));

    [TestMethod]
    public async Task Remote_default_branch_is_detected()
    {
        using var box = new GitSandbox();

        Assert.AreEqual("develop", await new GitRemote(box.Git).GetDefaultBranchAsync(box.RemotePath, default));
    }

    [TestMethod]
    public void Default_branch_parsing() =>
        Assert.AreEqual("main", GitRemote.ParseDefaultBranch("ref: refs/heads/main\tHEAD\nabc123\tHEAD"));

    [TestMethod]
    public async Task Git_failures_surface_stderr()
    {
        using var box = new GitSandbox();

        var ex = await Assert.ThrowsExactlyAsync<GitException>(() => new GitRemote(box.Git).GetDefaultBranchAsync(Path.Combine(box.Root, "missing.git"), default));

        Assert.IsFalse(string.IsNullOrWhiteSpace(ex.StandardError));
    }

    [TestMethod]
    public async Task The_diff_shows_uncommitted_edits_in_the_worktree_and_the_branch_once_it_is_removed()
    {
        using var box = new GitSandbox();
        var worktree = await box.Manager.CreateAsync(box.Repository, s_wi, s_branch, default);
        GitSandbox.Commit(worktree, "fix.txt", "fixed\n");
        await File.WriteAllTextAsync(Path.Combine(worktree.Value, "fix.txt"), "fixed again\n");

        var live = await box.Manager.DiffAsync(box.Repository, s_branch, worktree, 1_000_000, default);

        Assert.AreEqual(("origin/develop", "ai/1234-fix-login", false), (live!.BaseRef, live.HeadRef, live.Truncated));
        CollectionAssert.AreEqual(new[] { "fix.txt" }, live.Files.ToArray());
        StringAssert.Contains(live.UnifiedDiff, "+fixed again");

        GitSandbox.Run(worktree.Value, "checkout", "--", "fix.txt");
        await box.Manager.RemoveAsync(box.Repository, worktree, default);
        var archived = await box.Manager.DiffAsync(box.Repository, s_branch, worktree, 1_000_000, default);
        StringAssert.Contains(archived!.UnifiedDiff, "+fixed");
        Assert.IsFalse(archived.UnifiedDiff!.Contains("again", StringComparison.Ordinal), "only committed work survives the worktree");
    }

    [TestMethod]
    public async Task A_big_diff_returns_only_the_file_list_and_a_missing_branch_is_null()
    {
        using var box = new GitSandbox();
        var worktree = await box.Manager.CreateAsync(box.Repository, s_wi, s_branch, default);
        GitSandbox.Commit(worktree, "big.txt", string.Concat(Enumerable.Repeat("line\n", 200)));

        var diff = await box.Manager.DiffAsync(box.Repository, s_branch, worktree, 500, default);

        Assert.IsTrue(diff!.Truncated);
        Assert.IsNull(diff.UnifiedDiff);
        CollectionAssert.AreEqual(new[] { "big.txt" }, diff.Files.ToArray());
        Assert.IsNull(await box.Manager.DiffAsync(box.Repository, BranchName.For(WorkItemId.From(9), "nope"), null, 500, default));
    }

    [TestMethod]
    public async Task A_detached_checkout_follows_the_base_branch_and_is_reused()
    {
        using var box = new GitSandbox();

        var path = await box.Manager.CheckoutDetachedAsync(box.Repository, "idea-7", default);
        var again = await box.Manager.CheckoutDetachedAsync(box.Repository, "idea-7", default);

        Assert.AreEqual(path, again);
        StringAssert.EndsWith(path, Path.Combine("worktrees", "sysmin", "idea-7"));
        Assert.AreEqual("HEAD", GitSandbox.Run(path, "rev-parse", "--abbrev-ref", "HEAD"), "detached: no branch to commit to");
        Assert.AreEqual(GitSandbox.Run(box.RemotePath, "rev-parse", "develop"), GitSandbox.Run(path, "rev-parse", "HEAD"));
    }
}
