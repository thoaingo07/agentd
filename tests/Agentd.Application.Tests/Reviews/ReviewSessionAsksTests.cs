using Agentd.Application.Ideas;
using Agentd.Application.Reviews;
using static Agentd.Application.Tests.Reviews.ReviewSessionServiceTests;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Reviews;

[TestClass]
public sealed class ReviewSessionAsksTests
{
    private readonly TestContext _t = new();
    private readonly MemoryReviewSessions _store = new();
    private readonly Agent _agent = new();

    [TestMethod]
    public async Task A_question_on_lines_is_answered_from_the_reviews_checkout()
    {
        var id = await SessionAsync();
        _agent.Reply = new BrainstormReply("It pages by key: see `src/Sync.cs:41`.", null, null);
        using var asks = Asks();

        var ask = (await asks.AskAsync(id, " src/Sync.cs ", 41, 45, "  why a loop here? ", "dev@example.com", default)).Value!;
        await asks.Answer(id, ask);

        var answered = (await _store.ListAsksAsync(id, default)).Single();
        Assert.AreEqual(("why a loop here?", "src/Sync.cs", "It pages by key: see `src/Sync.cs:41`."), (answered.Question, answered.File, answered.Answer));
        Assert.AreEqual(("review-session-1", "h1"), _t.Worktrees.CheckedOutCommits.Single(), "the review's own checkout, at its head");
        var turn = _agent.Turns.Single();
        Assert.AreEqual(ThreadTurnKind.ReviewAsk, turn.Kind);
        StringAssert.Contains(turn.Prompt, "dev@example.com selected src/Sync.cs:41-45 and asks:\n\nwhy a loop here?");
        StringAssert.Contains(turn.Prompt, "git diff m1 h1");
    }

    [TestMethod]
    public async Task A_failed_answer_says_so_instead_of_waiting_forever()
    {
        var id = await SessionAsync();
        _agent.Reply = new BrainstormReply(null, null, "exit 1");
        using var asks = Asks();

        var ask = (await asks.AskAsync(id, null, null, null, "what does this change do?", "dev@example.com", default)).Value!;
        await asks.Answer(id, ask);

        StringAssert.StartsWith((await _store.ListAsksAsync(id, default)).Single().Answer, "⚠️ I couldn't answer (exit 1)");
        StringAssert.Contains(_agent.Turns.Single().Prompt, "selected the whole change");
    }

    [TestMethod]
    public async Task A_follow_up_resumes_the_threads_session_after_the_answer()
    {
        var id = await SessionAsync();
        _agent.Reply = new BrainstormReply("It pages by key.", null, null);
        _agent.Gate = new TaskCompletionSource();
        using var asks = Asks();
        var first = (await asks.AskAsync(id, "src/Sync.cs", 41, null, "why a loop here?", "dev@example.com", default)).Value!;

        var tooSoon = await asks.FollowUpAsync(id, first.Id, "and if it's empty?", "dev@example.com", default);
        _agent.Gate.SetResult();
        await asks.Answer(id, first);
        var follow = (await asks.FollowUpAsync(id, first.Id, " and if it's empty? ", "lead@example.com", default)).Value!;
        await asks.Answer(id, follow);
        var missing = await asks.FollowUpAsync(id, 999, "?", "dev@example.com", default);

        Assert.AreEqual(("conflict", "not_found"), (tooSoon.Error!.Code, missing.Error!.Code));
        Assert.AreEqual((first.Id, "src/Sync.cs", 41), (follow.ThreadId, follow.File, follow.Line), "a follow-up is at its thread's place");
        var (start, resume) = (_agent.Turns[0], _agent.Turns[1]);
        Assert.AreEqual((false, true, start.Session, first.Id), (start.Resume, resume.Resume, resume.Session, resume.IdeaId));
        Assert.AreEqual("lead@example.com follows up:\n\nand if it's empty?", resume.Prompt);
        Assert.AreEqual("It pages by key.", (await _store.ListAsksAsync(id, default)).Single(a => a.Id == follow.Id).Answer);
    }

    [TestMethod]
    public async Task A_follow_up_whose_session_is_gone_starts_over_with_the_conversation_so_far()
    {
        var id = await SessionAsync();
        _agent.Reply = new BrainstormReply("It pages by key.", null, null);
        using var asks = Asks();
        var first = (await asks.AskAsync(id, null, null, null, "what does this change do?", "dev@example.com", default)).Value!;
        await asks.Answer(id, first);
        var follow = (await asks.FollowUpAsync(id, first.Id, "is it safe?", "dev@example.com", default)).Value!;
        _agent.Replies.Enqueue(new BrainstormReply(null, null, "No conversation found with session ID"));

        await asks.Answer(id, follow);

        var retry = _agent.Turns[^1];
        Assert.IsFalse(retry.Resume);
        StringAssert.Contains(retry.Prompt, "asks:\n\nwhat does this change do?\n\nYou answered:\n\nIt pages by key.\n\ndev@example.com follows up:\n\nis it safe?");
        Assert.AreEqual(retry.Session, (await _store.ListAsksAsync(id, default)).Single(a => a.Id == first.Id).AgentSession, "later follow-ups resume the new session");
        Assert.AreEqual("It pages by key.", (await _store.ListAsksAsync(id, default)).Single(a => a.Id == follow.Id).Answer);
    }

    [TestMethod]
    public async Task A_question_needs_text_real_lines_and_an_open_review()
    {
        var id = await SessionAsync();
        using var asks = Asks();

        var empty = await asks.AskAsync(id, null, null, null, " ", "dev@example.com", default);
        var lines = await asks.AskAsync(id, "src/A.cs", 5, 3, "why?", "dev@example.com", default);
        var missing = await asks.AskAsync(999, null, null, null, "why?", "dev@example.com", default);
        await _store.SetStatusAsync(id, ReviewSessionStatus.Closed, null, null, default);
        var closed = await asks.AskAsync(id, null, null, null, "why?", "dev@example.com", default);

        Assert.AreEqual(("validation", "validation", "not_found", "conflict"), (empty.Error!.Code, lines.Error!.Code, missing.Error!.Code, closed.Error!.Code));
        Assert.IsEmpty(_agent.Turns);
    }

    private async Task<long> SessionAsync()
    {
        var id = await _store.InsertAsync("sysmin", ReviewTargets.Branch, null, "feature/keyset", "develop", "dev@example.com", "claude-opus-5-5", "high", default);
        await _store.PinAsync(id, "m1", "h1", null, default);
        await _store.SetStatusAsync(id, ReviewSessionStatus.Ready, null, null, default);
        return id;
    }

    private ReviewSessionAsks Asks() => new(_store, _t.Registry, _t.Worktrees, _agent);

    private sealed class Agent : IBrainstormAgent
    {
        public BrainstormReply Reply { get; set; } = new(null, null, null);

        /// <summary>Replies used once each, before <see cref="Reply"/>.</summary>
        public Queue<BrainstormReply> Replies { get; } = [];

        public List<BrainstormTurn> Turns { get; } = [];

        /// <summary>Turns wait for this, when set.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public async Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
        {
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            lock (Turns)
            {
                Turns.Add(turn);
                return Replies.TryDequeue(out var next) ? next : Reply;
            }
        }
    }
}
