using Agentd.Application.Ports;
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
    public async Task Each_worktree_commits_as_its_own_author_and_null_goes_back_to_agentd()
    {
        using var box = new GitSandbox();
        var mine = await box.Manager.CreateAsync(box.Repository, s_wi, s_branch, default);
        var other = WorkItemId.From(99);
        var theirs = await box.Manager.CreateAsync(box.Repository, other, BranchName.For(other, "other"), default);
        var agentd = GitSandbox.Run(theirs.Value, "config", "user.name");

        await box.Manager.SetCommitAuthorAsync(mine, new CommitAuthor("Dev One", "dev.one@example.com"), default);
        GitSandbox.Run(mine.Value, "commit", "--allow-empty", "-m", "as the assignee");
        GitSandbox.Run(theirs.Value, "commit", "--allow-empty", "-m", "as agentd");
        var fetched = await box.Manager.ResolveCommitAsync(box.Repository, "develop", default);   // the clone still works as a bare repository

        Assert.AreEqual("Dev One <dev.one@example.com>", GitSandbox.Run(mine.Value, "log", "-1", "--format=%an <%ae>"));
        Assert.AreEqual(agentd, GitSandbox.Run(theirs.Value, "log", "-1", "--format=%an"), "other worktrees keep agentd's");
        Assert.IsNotNull(fetched);

        await box.Manager.SetCommitAuthorAsync(mine, null, default);
        Assert.AreEqual(agentd, GitSandbox.Run(mine.Value, "config", "user.name"));
        await box.Manager.SetCommitAuthorAsync(mine, null, default);   // already agentd's: no error
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
    public async Task A_pushed_branch_resolves_to_its_commit_and_diffs_from_where_it_left_the_base()
    {
        using var box = new GitSandbox();
        // A colleague's branch, pushed from another clone, while develop moves on.
        var other = Path.Combine(box.Root, "colleague");
        GitSandbox.Run(box.Root, "clone", box.RemotePath, other);
        GitSandbox.Run(other, "checkout", "-b", "feature/keyset");
        File.WriteAllText(Path.Combine(other, "Sync.cs"), "class Sync {}\n");
        GitSandbox.Run(other, "add", ".");
        GitSandbox.Run(other, "-c", "user.name=c", "-c", "user.email=c@x", "commit", "-m", "keyset");
        GitSandbox.Run(other, "push", "origin", "feature/keyset");
        GitSandbox.Run(other, "checkout", "develop");
        File.WriteAllText(Path.Combine(other, "Later.cs"), "class Later {}\n");
        GitSandbox.Run(other, "add", ".");
        GitSandbox.Run(other, "-c", "user.name=c", "-c", "user.email=c@x", "commit", "-m", "later on develop");
        GitSandbox.Run(other, "push", "origin", "develop");

        await box.Manager.EnsureCloneAsync(box.Repository, default);
        var head = await box.Manager.ResolveCommitAsync(box.Repository, "feature/keyset", default);
        var develop = await box.Manager.ResolveCommitAsync(box.Repository, "develop", default);
        var mergeBase = await box.Manager.MergeBaseAsync(box.Repository, develop!, head!, default);
        var diff = await box.Manager.DiffCommitsAsync(box.Repository, mergeBase!, head!, 1 << 20, default);

        Assert.AreEqual(GitSandbox.Run(other, "rev-parse", "feature/keyset"), head);
        Assert.AreEqual(head, await box.Manager.ResolveCommitAsync(box.Repository, head![..8], default), "an abbreviated commit too");
        CollectionAssert.AreEqual(new[] { "Sync.cs" }, diff.Files.ToList(), "develop's later commit isn't part of the branch's change");
        StringAssert.Contains(diff.UnifiedDiff, "+class Sync {}");
        Assert.IsNull(await box.Manager.ResolveCommitAsync(box.Repository, "no-such-branch", default));
        Assert.IsNull(await box.Manager.ResolveCommitAsync(box.Repository, "--output=/tmp/x", default), "never an option");
        Assert.IsNull(await box.Manager.ResolveCommitAsync(box.Repository, "develop..feature/keyset", default), "never a range");
        Assert.IsTrue((await box.Manager.DiffCommitsAsync(box.Repository, mergeBase!, head!, 10, default)).Truncated, "over the cap: the file list only");
    }

    [TestMethod]
    public async Task A_fix_commits_as_agentd_pushes_onto_the_branch_and_a_branch_that_moved_refuses_it()
    {
        using var box = new GitSandbox();
        var other = Path.Combine(box.Root, "colleague");
        GitSandbox.Run(box.Root, "clone", box.RemotePath, other);
        GitSandbox.Run(other, "checkout", "-b", "feature/x");
        File.WriteAllText(Path.Combine(other, "A.cs"), "class A {}\n");
        GitSandbox.Run(other, "add", ".");
        GitSandbox.Run(other, "-c", "user.name=c", "-c", "user.email=c@x", "commit", "-m", "a");
        GitSandbox.Run(other, "push", "origin", "feature/x");
        await box.Manager.EnsureCloneAsync(box.Repository, default);
        var head = (await box.Manager.ResolveCommitAsync(box.Repository, "feature/x", default))!;

        var path = await box.Manager.CheckoutCommitAsync(box.Repository, "review-fix-1", head, default);
        var nothing = await box.Manager.CommitAllAsync(path, "fix: nothing", default);
        File.WriteAllText(Path.Combine(path, "A.cs"), "class A { int fixedIt; }\n");
        var commit = await box.Manager.CommitAllAsync(path, "fix: address review findings", default);
        await box.Manager.PushHeadAsync(path, "feature/x", default);

        Assert.IsNull(nothing, "no changes, no commit");
        Assert.AreEqual(commit, GitSandbox.Run(box.Root, "--git-dir", box.RemotePath, "rev-parse", "feature/x"), "the branch now has the fix");
        Assert.AreEqual("agentd", GitSandbox.Run(path, "log", "-1", "--format=%an"));

        // The colleague pushes meanwhile; a second fix from the stale checkout must not overwrite it.
        GitSandbox.Run(other, "pull", "-q", "origin", "feature/x");
        File.WriteAllText(Path.Combine(other, "B.cs"), "class B {}\n");
        GitSandbox.Run(other, "add", ".");
        GitSandbox.Run(other, "-c", "user.name=c", "-c", "user.email=c@x", "commit", "-m", "b");
        GitSandbox.Run(other, "push", "origin", "feature/x");
        File.WriteAllText(Path.Combine(path, "A.cs"), "class A { int again; }\n");
        await box.Manager.CommitAllAsync(path, "fix: again", default);

        await Assert.ThrowsAsync<Exception>(() => box.Manager.PushHeadAsync(path, "feature/x", default));
        await Assert.ThrowsAsync<Exception>(() => box.Manager.PushHeadAsync(path, "--force", default));
        CollectionAssert.DoesNotContain(GitWorktreeManager.BuildPushHeadArgs("feature/x").ToList(), "--force");
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

    [TestMethod]
    public async Task A_commit_is_checked_out_detached_and_moved_when_the_pr_gets_new_commits()
    {
        using var box = new GitSandbox();
        var seed = Path.Combine(box.Root, "seed");
        GitSandbox.Run(seed, "checkout", "-b", "feature/x");
        File.WriteAllText(Path.Combine(seed, "a.txt"), "one\n");
        GitSandbox.Run(seed, "add", ".");
        GitSandbox.Run(seed, "-c", "user.name=dev", "-c", "user.email=dev@x", "commit", "-m", "one");
        GitSandbox.Run(seed, "push", "origin", "feature/x");
        var first = GitSandbox.Run(seed, "rev-parse", "HEAD");

        var path = await box.Manager.CheckoutCommitAsync(box.Repository, "review-3", first, default);
        File.WriteAllText(Path.Combine(seed, "a.txt"), "two\n");
        GitSandbox.Run(seed, "-c", "user.name=dev", "-c", "user.email=dev@x", "commit", "-am", "two");
        GitSandbox.Run(seed, "push", "origin", "feature/x");
        var second = GitSandbox.Run(seed, "rev-parse", "HEAD");
        var again = await box.Manager.CheckoutCommitAsync(box.Repository, "review-3", second, default);

        Assert.AreEqual(path, again);
        Assert.AreEqual(("HEAD", second), (GitSandbox.Run(path, "rev-parse", "--abbrev-ref", "HEAD"), GitSandbox.Run(path, "rev-parse", "HEAD")), "detached at the new head");
        Assert.AreEqual("two", File.ReadAllText(Path.Combine(path, "a.txt")).Trim());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => box.Manager.CheckoutCommitAsync(box.Repository, "review-3", "--upload-pack=evil", default));
    }
}

