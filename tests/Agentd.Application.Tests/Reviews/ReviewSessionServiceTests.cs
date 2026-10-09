using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Application.Reviews;
using Microsoft.Extensions.Options;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Reviews;

[TestClass]
public sealed class ReviewSessionServiceTests
{
    private readonly TestContext _t = new();
    private readonly MemoryReviewSessions _store = new();

    [TestMethod]
    public async Task A_branch_is_fetched_and_pinned_from_where_it_left_its_base()
    {
        Branch("feature/keyset", head: "h1", baseTip: "d9", mergeBase: "m1");

        var session = (await Service().StartAsync(new StartReview("sysmin", Branch: "feature/keyset"), "dev@example.com", default)).Value!;

        Assert.AreEqual((ReviewTargets.Branch, "feature/keyset", "develop", "m1", "h1"), (session.Target, session.HeadRef, session.BaseRef, session.BaseCommit, session.HeadCommit));
        Assert.AreEqual((ReviewSessionStatus.Ready, "dev@example.com", "claude-opus-5-5", "high"), (session.Status, session.CreatedBy, session.Model, session.Effort));
        CollectionAssert.Contains(_t.Worktrees.Cloned, "sysmin", "fetched first: the latest branches");
        await Service().DiffAsync(session.Id, default);
        Assert.AreEqual(("m1", "h1"), _t.Worktrees.CommitDiffs.Single(), "the diff always uses the pinned commits");
    }

    [TestMethod]
    public async Task A_pr_is_pinned_at_its_head_against_its_target()
    {
        _t.PullRequests.Details[3944] = new PullRequestDetails(3944, "Deploy", null, "Dev", "ai/5617-deploy", "develop", "c0ffee1234", PullRequestStatus.Active, false, new Uri("https://x/pr/3944"));
        _t.Worktrees.Commits["c0ffee1234"] = "c0ffee1234full";
        _t.Worktrees.Commits["develop"] = "d9";
        _t.Worktrees.MergeBases[("d9", "c0ffee1234full")] = "m2";

        var session = (await Service().StartAsync(new StartReview("sysmin", PullRequestId: 3944), "dev@example.com", default)).Value!;

        Assert.AreEqual((ReviewTargets.PullRequest, (int?)3944, "ai/5617-deploy", "develop", "m2", "c0ffee1234full"),
            (session.Target, session.PullRequestId, session.HeadRef, session.BaseRef, session.BaseCommit, session.HeadCommit));
    }

    [TestMethod]
    [DataRow(null, null, null, null, "Say what to review")]
    [DataRow(3944, "feature/x", null, null, "Say what to review")]
    [DataRow(null, null, null, "h1", "Say what to review")]
    [DataRow(null, "no-such", null, null, "is it pushed?")]
    public async Task A_review_needs_exactly_one_thing_that_exists(int? pr, string? branch, string? @base, string? head, string expected)
    {
        var result = await Service().StartAsync(new StartReview("sysmin", pr, branch, @base, head), "dev@example.com", default);

        StringAssert.Contains(result.Error!.Message, expected);
        Assert.IsEmpty(_store.Sessions);
    }

    [TestMethod]
    public async Task Decisions_and_comments_are_checked_and_stop_once_the_review_is_sent()
    {
        Branch("feature/keyset", head: "h1", baseTip: "d9", mergeBase: "m1");
        var id = (await Service().StartAsync(new StartReview("sysmin", Branch: "feature/keyset"), "dev@example.com", default)).Value!.Id;
        await _store.AddFindingsAsync(id, [new SessionFinding("breaks", "src/A.cs", 3, "Unbounded read", null, null)], null, default);

        var edited = await Service().DecideAsync(id, 1, FindingDecisions.Edited, "  say it plainly ", default);
        var noText = await Service().DecideAsync(id, 1, FindingDecisions.Edited, " ", default);
        var unknown = await Service().DecideAsync(id, 1, "maybe", null, default);
        var missing = await Service().DecideAsync(id, 2, FindingDecisions.Dropped, null, default);
        var comment = await Service().AddCommentAsync(id, "src/A.cs", 3, 5, "  make it config ", "dev@example.com", default);
        var badLines = await Service().AddCommentAsync(id, "src/A.cs", 5, 3, "x", "dev@example.com", default);
        var lineWithoutFile = await Service().AddCommentAsync(id, null, 3, null, "x", "dev@example.com", default);
        var notMine = await Service().DeleteCommentAsync(id, comment.Value!.Id, "lead@example.com", default);
        await _store.SetStatusAsync(id, ReviewSessionStatus.Sent, null, "pr:3944", default);
        var late = await Service().AddCommentAsync(id, null, null, null, "too late", "dev@example.com", default);

        Assert.IsTrue(edited.IsSuccess);
        Assert.AreEqual((FindingDecisions.Edited, "say it plainly"), (_store.Sessions[id].Findings[0].Decision, _store.Sessions[id].Findings[0].Edited));
        Assert.AreEqual(("validation", "validation", "not_found"), (noText.Error!.Code, unknown.Error!.Code, missing.Error!.Code));
        Assert.AreEqual(("make it config", 3, 5), (comment.Value.Text, comment.Value.Line, comment.Value.EndLine));
        Assert.AreEqual(("validation", "validation", "not_found", "conflict"), (badLines.Error!.Code, lineWithoutFile.Error!.Code, notMine.Error!.Code, late.Error!.Code));
    }

    private void Branch(string name, string head, string baseTip, string mergeBase)
    {
        _t.Worktrees.Commits[name] = head;
        _t.Worktrees.Commits["develop"] = baseTip;
        _t.Worktrees.MergeBases[(baseTip, head)] = mergeBase;
    }

    private ReviewSessionService Service()
    {
        var jobs = new JobOptions();
        jobs.Steps[JobSteps.Review] = new StepModel { Model = "claude-opus-5-5", Effort = "High" };
        return new(_store, _t.Registry, _t.Worktrees, _t.PullRequests, Options.Create(jobs));
    }

    /// <summary>Review sessions in memory, with the routines' rules (append findings, decide in range, author-only delete).</summary>
    internal sealed class MemoryReviewSessions : IReviewSessionStore
    {
        private readonly List<ReviewComment> _comments = [];
        private readonly Dictionary<long, long> _sessionOf = [];
        private long _next;

        public Dictionary<long, ReviewSession> Sessions { get; } = [];

        public Task<long> InsertAsync(string repository, string target, int? pullRequestId, string? headRef, string? baseRef, string createdBy, string? model, string? effort, CancellationToken cancellationToken)
        {
            var id = ++_next;
            Sessions[id] = new ReviewSession(id, repository, target, pullRequestId, headRef, baseRef, null, null, ReviewSessionStatus.Reviewing, null, model, effort, null, [], null, null, createdBy, null, default, default);
            return Task.FromResult(id);
        }

        public Task<ReviewSession?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(Sessions.GetValueOrDefault(id));

        public Task<IReadOnlyList<ReviewSession>> ListAsync(string createdBy, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReviewSession>>([.. Sessions.Values.Where(s => s.CreatedBy == createdBy).OrderByDescending(s => s.Id).Take(limit)]);

        public Task<IReadOnlyList<ReviewSession>> ListByStatusAsync(string status, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReviewSession>>([.. Sessions.Values.Where(s => s.Status == status).OrderBy(s => s.Id)]);

        public Task PinAsync(long id, string baseCommit, string headCommit, string? worktree, CancellationToken cancellationToken)
        {
            Sessions[id] = Sessions[id] with { BaseCommit = baseCommit, HeadCommit = headCommit, Worktree = worktree };
            return Task.CompletedTask;
        }

        public Task SetStatusAsync(long id, string status, string? reason, string? sentTo, CancellationToken cancellationToken)
        {
            Sessions[id] = Sessions[id] with { Status = status, Error = reason, SentTo = sentTo ?? Sessions[id].SentTo };
            return Task.CompletedTask;
        }

        public Task<int> AddFindingsAsync(long id, IReadOnlyList<SessionFinding> findings, string? summary, CancellationToken cancellationToken)
        {
            Sessions[id] = Sessions[id] with { Findings = [.. Sessions[id].Findings, .. findings], Summary = summary ?? Sessions[id].Summary };
            return Task.FromResult(Sessions[id].Findings.Count);
        }

        public Task<bool> DecideAsync(long id, int index, string decision, string? edited, CancellationToken cancellationToken)
        {
            var findings = Sessions[id].Findings.ToList();
            if (index < 0 || index >= findings.Count)
            {
                return Task.FromResult(false);
            }

            findings[index] = findings[index] with { Decision = decision, Edited = edited };
            Sessions[id] = Sessions[id] with { Findings = findings };
            return Task.FromResult(true);
        }

        public Task<long> AddCommentAsync(long sessionId, string? file, int? line, int? endLine, string text, string author, CancellationToken cancellationToken)
        {
            var id = ++_next;
            _comments.Add(new ReviewComment(id, file, line, endLine, text, author, default));
            _sessionOf[id] = sessionId;
            return Task.FromResult(id);
        }

        public Task<bool> DeleteCommentAsync(long sessionId, long commentId, string author, CancellationToken cancellationToken) =>
            Task.FromResult(_sessionOf.GetValueOrDefault(commentId) == sessionId && _comments.RemoveAll(c => c.Id == commentId && c.Author == author) > 0);

        public Task<IReadOnlyList<ReviewComment>> ListCommentsAsync(long sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReviewComment>>([.. _comments.Where(c => _sessionOf[c.Id] == sessionId)]);

        public Task<long> AddAskAsync(long sessionId, string? file, int? line, int? endLine, string question, string author, CancellationToken cancellationToken) => Task.FromResult(++_next);

        public Task AnswerAsync(long askId, string answer, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ReviewAsk>> ListAsksAsync(long sessionId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ReviewAsk>>([]);
    }
}
