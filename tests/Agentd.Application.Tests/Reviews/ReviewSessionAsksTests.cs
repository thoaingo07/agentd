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

        public List<BrainstormTurn> Turns { get; } = [];

        public Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
        {
            lock (Turns)
            {
                Turns.Add(turn);
            }

            return Task.FromResult(Reply);
        }
    }
}
