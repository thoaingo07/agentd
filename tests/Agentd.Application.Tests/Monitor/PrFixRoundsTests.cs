using Agentd.Application.Ideas;
using Agentd.Application.Messaging;
using Agentd.Application.Monitor;
using Agentd.Application.Ports;
using Agentd.Domain.Messaging;
using Microsoft.Extensions.Options;
using static Agentd.Application.Tests.Monitor.PrWatchServiceTests;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Monitor;

[TestClass]
public sealed class PrFixRoundsTests
{
    private const string Checkout = "/home/agentd/.agentd/worktrees/sysmin/pr-fix-1";
    private readonly TestContext _t = new();
    private readonly MemoryWatches _watches = new();
    private readonly Agent _agent = new();
    private readonly Fakes.FakeChat _chat = new("discord");
    private readonly PullRequestDetails _pr = new(3944, "Deploy", null, "Dev", "feature/x", "develop", "h1", PullRequestStatus.Active, false, new Uri("https://x/pr/3944"));

    public PrFixRoundsTests()
    {
        _t.Chats.Add(_chat);
        _t.Messaging.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
        _t.PullRequests.Details[3944] = _pr;
        _watches.InsertAsync("sysmin", 3944, "Deploy", "tngo", ProviderKey.From("discord"), "t-1", null, [], default).GetAwaiter().GetResult();
    }

    [TestMethod]
    public async Task A_round_fixes_a_checkout_of_the_pr_head_commits_and_asks_before_pushing()
    {
        using var rounds = Rounds();

        await rounds.PrepareAsync(Request(build: true, comments: [Comment(11, 7), Comment(12, 8)]), default);

        Assert.AreEqual(("pr-fix-1", "h1"), _t.Worktrees.CheckedOutCommits.Single());
        Assert.IsEmpty(_t.Worktrees.Merged, "no conflicts: no merge");
        var turn = _agent.Turns.Single();
        Assert.AreEqual(ThreadTurnKind.ReviewFix, turn.Kind);
        StringAssert.Contains(turn.Prompt, "## The PR build failed (sysmin-ci, run 901)");
        StringAssert.Contains(turn.Prompt, "- dotnet test: exit code 1");
        StringAssert.Contains(turn.Prompt, "- Kelvin (src/A.cs:3): comment 11");
        Assert.AreEqual("fix: the PR build, 2 review comment(s) (agentd PR Monitor, PR !3944)", _t.Worktrees.CommittedAll.Single().Message);
        var pending = _watches.All.Single().Pending!;
        Assert.AreEqual((Checkout, "c0ffee1", _t.Clock.UtcNow + PrFixRounds.Expiry), (pending.Worktree, pending.Commit, pending.ExpiresAt));
        CollectionAssert.AreEqual(new[] { 7, 8 }, pending.Threads.ToList());
        StringAssert.Contains(_chat.SentText[^1], "**Fix ready for PR !3944** (c0ffee1): Fixed the test.");
        StringAssert.Contains(_chat.SentText[^1], "**1** push to `feature/x` · **2** discard");
        Assert.IsEmpty(_t.Worktrees.PushedHeads, "nothing is pushed before someone says so");
        Assert.IsEmpty(_t.Worktrees.Removed, "the checkout waits for the answer");
    }

    [TestMethod]
    public async Task Conflicts_merge_the_target_branch_and_markers_left_behind_give_up()
    {
        using var rounds = Rounds();
        _t.Worktrees.MergeConflicts.AddRange(["src/A.cs", "src/B.cs"]);
        _t.Worktrees.LeftMarkers.Add("src/B.cs");

        await rounds.PrepareAsync(Request(conflicts: true), default);

        Assert.AreEqual((Checkout, "origin/develop"), _t.Worktrees.Merged.Single());
        StringAssert.Contains(_agent.Turns.Single().Prompt, "Resolve the conflict markers in: src/A.cs, src/B.cs");
        Assert.IsEmpty(_t.Worktrees.CommittedAll);
        Assert.IsNull(_watches.All.Single().Pending);
        StringAssert.Contains(_chat.SentText[^1], "I couldn't prepare a fix: conflict markers are still in `src/B.cs`");
        Assert.AreEqual(Checkout, _t.Worktrees.Removed.Single());
    }

    [TestMethod]
    public async Task Nothing_changed_keeps_nothing()
    {
        using var rounds = Rounds();
        _t.Worktrees.NextCommit = null;
        _agent.Reply = new BrainstormReply("The comment asks for something already there.", null, null);

        await rounds.PrepareAsync(Request(comments: [Comment(11, 7)]), default);

        Assert.IsNull(_watches.All.Single().Pending);
        StringAssert.Contains(_chat.SentText[^1], "the fixer changed nothing. It said: The comment asks for something already there.");
        Assert.AreEqual(Checkout, _t.Worktrees.Removed.Single());
    }

    [TestMethod]
    public async Task Push_goes_to_the_pr_branch_once_marks_the_threads_fixed_and_notes_it_even_for_parallel_answers()
    {
        using var rounds = Rounds();
        await rounds.PrepareAsync(Request(comments: [Comment(11, 7)]), default);
        var watch = _watches.All.Single();

        var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => rounds.PushAsync(watch, "tngo", default))));

        Assert.AreEqual((Checkout, "feature/x"), _t.Worktrees.PushedHeads.Single(), "one push for five answers");
        Assert.AreEqual(1, answers.Count(a => a.StartsWith("✅ Pushed c0ffee1 to `feature/x`", StringComparison.Ordinal)));
        Assert.AreEqual(4, answers.Count(a => a.StartsWith("There's no prepared fix waiting", StringComparison.Ordinal)));
        Assert.AreEqual((3944, 7, "Fixed in c0ffee1."), _t.PullRequests.Replies.Single());
        Assert.AreEqual(PullRequestThreadStatus.Fixed, _t.PullRequests.ThreadStatus[7]);
        StringAssert.Contains(_t.PullRequests.Threads.Single().Text, "🔧 Pushed c0ffee1 (PR Monitor fix round 1, approved by tngo in chat)");
        Assert.IsNull(_watches.All.Single().Pending);
        Assert.AreEqual(Checkout, _t.Worktrees.Removed.Single());
    }

    [TestMethod]
    public async Task A_refused_push_or_a_discard_drops_the_fix_and_its_checkout()
    {
        using var rounds = Rounds();
        await rounds.PrepareAsync(Request(build: true), default);
        _t.Worktrees.PushFailure = new InvalidOperationException("! [rejected] HEAD -> feature/x (fetch first)");

        var refused = await rounds.PushAsync(_watches.All.Single(), "tngo", default);
        await rounds.PrepareAsync(Request(build: true), default);
        var discarded = await rounds.DiscardAsync(_watches.All.Single(), "tngo said so", default);

        StringAssert.StartsWith(refused, "⚠️ The push to `feature/x` was refused (! [rejected]");
        Assert.AreEqual("🗑️ Discarded the fix for PR !3944 (tngo said so).", discarded);
        Assert.IsNull(_watches.All.Single().Pending);
        Assert.HasCount(2, _t.Worktrees.Removed);
        Assert.IsEmpty(_t.PullRequests.Replies);
    }

    [TestMethod]
    public async Task The_thread_answers_1_push_and_2_discard_and_anything_else_repeats_the_question()
    {
        using var rounds = Rounds();
        await rounds.PrepareAsync(Request(build: true), default);
        var service = new PrWatchService(_watches, _t.Registry, _t.PullRequests, Providers(), rounds);

        var other = await service.HandleThreadMessageAsync(_watches.All.Single(), "hmm?", "tngo", default);
        var pushed = await service.HandleThreadMessageAsync(_watches.All.Single(), "1", "tngo", default);

        Assert.AreEqual("A fix for PR !3944 (c0ffee1) waits: **1** push · **2** discard.", other);
        StringAssert.StartsWith(pushed, "✅ Pushed c0ffee1");
        await rounds.PrepareAsync(Request(build: true), default);
        StringAssert.StartsWith(await service.HandleThreadMessageAsync(_watches.All.Single(), "Discard.", "kelvin", default), "🗑️ Discarded the fix for PR !3944 (kelvin said so)");
    }

    private PrFixRounds Rounds() => new(_watches, _t.Registry, _t.Worktrees, _agent, _t.PullRequests, Providers(), _t.Clock);

    private MessagingProviderRegistry Providers() => new(_t.Chats, Options.Create(_t.Messaging));

    private PrFixRequest Request(bool build = false, bool conflicts = false, IReadOnlyList<PullRequestComment>? comments = null)
    {
        // The monitor counts the round before it starts.
        var w = _watches.All.Single() with { FixRounds = 1 };
        _watches.UpdateAsync(w.Id, 1, w.SeenComments, w.LastBuildId, null, w.Pending, default).GetAwaiter().GetResult();
        var hit = new BuildHit(901, "sysmin-ci", "1", "completed", "failed", "refs/pull/3944/merge", null, "pullRequest", "h1", null, null);
        return new PrFixRequest(w, _t.Registry.Repositories[0], _pr, build ? new BuildDetail(hit, [new BuildFailure("dotnet test", "Task", "failed", ["exit code 1"], "Failed X")]) : null,
            conflicts, comments ?? []);
    }

    private static PullRequestComment Comment(int id, int thread) =>
        new(thread, id, "Kelvin", $"comment {id}", "src/A.cs", 3, "active", DateTimeOffset.UnixEpoch, "kelvin@example.com");

    private sealed class Agent : IBrainstormAgent
    {
        public BrainstormReply Reply { get; set; } = new("Fixed the test.", null, null);

        public List<BrainstormTurn> Turns { get; } = [];

        public Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
        {
            Turns.Add(turn);
            return Task.FromResult(Reply);
        }
    }
}
