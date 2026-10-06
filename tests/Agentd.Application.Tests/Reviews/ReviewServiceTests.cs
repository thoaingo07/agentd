using System.Collections.Concurrent;
using Agentd.Application.Ideas;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Application.Reviews;
using Agentd.Application.Tests.Fakes;
using Agentd.Domain.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Application.Tests.Reviews;

[TestClass]
public sealed class ReviewServiceTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");

    private const string Findings = """
        Two things to look at.

        ```review-findings
        {"summary":"Adds a deploy pipeline. One real bug.","findings":[
          {"severity":"major","file":"charts/api/values.yaml","line":12,"title":"Readiness probe path is wrong","detail":"It points at /health.","suggestion":"Use /health/ready."},
          {"severity":"minor","file":"azure-pipelines.yml","line":40,"title":"Image tag isn't pinned"},
          {"severity":"nit","title":"PR description is empty"}]}
        ```
        """;

    [TestMethod]
    public async Task A_review_opens_a_thread_with_the_instructions_and_shows_numbered_findings()
    {
        var h = new Harness();
        h.Agent.Replies.Enqueue(Findings);

        var started = await h.Service.StartAsync(s_discord, "tngo", ["3944", "check", "the", "helm", "probes", "--model", "opus", "--focus", "security"], default);
        await h.WaitForSentAsync(2);

        StringAssert.Contains(started.Value, "review #1");
        Assert.AreEqual("🔍 Review: PR !3944: Deploy to AKS", h.Chat.Opened.Single().Name);
        var turn = h.Agent.Turns.Single();
        Assert.AreEqual((ThreadTurnKind.Review, "opus", false), (turn.Kind, turn.Model, turn.Resume));
        StringAssert.Contains(turn.Prompt, "Instructions from tngo: check the helm probes");
        StringAssert.Contains(turn.Prompt, "Focus on: security");
        StringAssert.Contains(turn.Prompt, "origin/develop");
        Assert.AreEqual(("review-1", "abc1234"), h.Worktrees.CheckedOutCommits.Single(), "a checkout of the PR head");
        Assert.AreEqual(ReviewStatus.Reviewed, h.Store.Rows[1].Status);
        StringAssert.Contains(h.Chat.SentText.Last(), "**1.** 🟠 major **Readiness probe path is wrong**");
        StringAssert.Contains(h.Chat.SentText.Last(), "`post 1,3`");
        Assert.IsEmpty(h.PullRequests.Threads, "nothing goes to the PR until someone chooses");
    }

    [TestMethod]
    public async Task People_can_ask_about_findings_drop_some_and_post_the_ones_they_pick()
    {
        var h = new Harness();
        await h.ReviewedAsync();
        h.Agent.Replies.Enqueue("Because the readiness probe gates traffic.");

        Assert.IsTrue(await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "why is 1 a problem?", default));
        await h.WaitForSentAsync(3);
        Assert.AreEqual(("tngo: why is 1 a problem?", true), (h.Agent.Turns[1].Prompt, h.Agent.Turns[1].Resume), "a question goes to the same session");

        await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "drop 2", default);
        Assert.HasCount(2, h.Store.Rows[1].Result!.Findings);
        StringAssert.Contains(h.Chat.SentText.Last(), "Dropped #2");

        await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "post 1", default);

        var (pr, text, file, line) = h.PullRequests.Threads[0];
        Assert.AreEqual((3944, "charts/api/values.yaml", (int?)12), (pr, file, line), "anchored to the finding's file and line");
        StringAssert.Contains(text, "🟠 major: Readiness probe path is wrong");
        Assert.HasCount(2, h.PullRequests.Threads, "the picked finding and a summary, nothing else");
        StringAssert.Contains(h.PullRequests.Threads[1].Text, "Adds a deploy pipeline");
        Assert.AreEqual(ReviewStatus.Posted, h.Store.Rows[1].Status);
        StringAssert.Contains(h.Chat.SentText[^2], "Posted 1 finding(s)");
        StringAssert.Contains(h.Chat.SentText.Last(), "Delete this thread?");
    }

    [TestMethod]
    public async Task Keep_and_discard_post_nothing_and_finished_reviews_close_out()
    {
        var h = new Harness();
        await h.ReviewedAsync();

        await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "2", default);

        Assert.AreEqual(ReviewStatus.Kept, h.Store.Rows[1].Status);
        Assert.IsEmpty(h.PullRequests.Threads);
        await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "2", default);   // the close-out: keep (archive)
        Assert.AreEqual(ReviewStatus.Closed, h.Store.Rows[1].Status);
        Assert.IsFalse(await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "one more thing", default));
    }

    [TestMethod]
    public async Task A_pr_url_names_its_repository_and_closed_or_unknown_prs_are_refused()
    {
        var h = new Harness();
        h.Agent.Replies.Enqueue("ok");

        Assert.IsTrue((await h.Service.StartAsync(s_discord, "tngo", ["https://dev.azure.com/ermsystem/Portal/_git/sysmin/pullrequest/3944"], default)).IsSuccess);
        StringAssert.Contains((await h.Service.StartAsync(s_discord, "tngo", ["https://dev.azure.com/other/X/_git/y/pullrequest/1"], default)).Error!.Message, "!repo add");
        StringAssert.Contains((await h.Service.StartAsync(s_discord, "tngo", ["9"], default)).Error!.Message, "PR !9");
        h.PullRequests.Details[3944] = h.PullRequests.Details[3944] with { Status = PullRequestStatus.Completed };
        StringAssert.Contains((await h.Service.StartAsync(s_discord, "tngo", ["!3944"], default)).Error!.Message, "completed");
        StringAssert.Contains((await h.Service.StartAsync(s_discord, "tngo", ["the", "login", "page"], default)).Error!.Message, "Which PR?");
    }

    [TestMethod]
    [DataRow("1", ReviewChoiceKind.Post, "")]
    [DataRow("post all", ReviewChoiceKind.Post, "")]
    [DataRow("post 1,3", ReviewChoiceKind.Post, "1,3")]
    [DataRow("post #3 1", ReviewChoiceKind.Post, "1,3")]
    [DataRow("drop 2", ReviewChoiceKind.Drop, "2")]
    [DataRow("2", ReviewChoiceKind.Keep, "")]
    [DataRow("Discard", ReviewChoiceKind.Discard, "")]
    public void Choices_are_read_from_numbers_and_words(string answer, ReviewChoiceKind kind, string numbers)
    {
        var choice = ReviewChoice.Parse(answer, 3)!;

        Assert.AreEqual(kind, choice.Kind);
        Assert.AreEqual(numbers, string.Join(',', choice.Numbers));
    }

    [TestMethod]
    [DataRow("post 4")]
    [DataRow("drop 0")]
    [DataRow("why is 2 a problem?")]
    [DataRow("post the first one")]
    public void Anything_else_is_a_question_for_the_agent(string answer) => Assert.IsNull(ReviewChoice.Parse(answer, 3));

    private sealed class Harness
    {
        public Harness()
        {
            var options = Microsoft.Extensions.Options.Options.Create(new MessagingOptions());
            options.Value.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
            PullRequests.Details[3944] = new PullRequestDetails(3944, "Deploy to AKS", "Pipelines for test and prod.", "Dev One", "ai/5617-deploy", "develop", "abc1234",
                PullRequestStatus.Active, false, new Uri("https://dev.azure.com/ermsystem/Portal/_git/sysmin/pullrequest/3944"));
            Service = new ReviewService(Store, Registry, PullRequests, Worktrees, Agent, new MessagingProviderRegistry([Chat], options), NullLogger<ReviewService>.Instance);
        }

        public FakeChat Chat { get; } = new("discord");

        public FakeRegistry Registry { get; } = new();

        public FakeWorktrees Worktrees { get; } = new();

        public FakePullRequests PullRequests { get; } = new();

        public Agent Agent { get; } = new();

        public Store Store { get; } = new();

        public ReviewService Service { get; }

        public async Task ReviewedAsync()
        {
            Agent.Replies.Enqueue(Findings);
            await Service.StartAsync(ProviderKey.From("discord"), "tngo", ["3944"], default);
            await WaitForSentAsync(2);
        }

        public async Task WaitForSentAsync(int count)
        {
            for (var i = 0; i < 300 && Chat.SentText.Count < count; i++)
            {
                await Task.Delay(10);
            }

            Assert.IsGreaterThanOrEqualTo(count, Chat.SentText.Count, "the reply was posted");
        }
    }

    private sealed class Agent : IBrainstormAgent
    {
        public ConcurrentQueue<string> Replies { get; } = new();

        public List<BrainstormTurn> Turns { get; } = [];

        public Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
        {
            Turns.Add(turn);
            return Task.FromResult(new BrainstormReply(Replies.TryDequeue(out var r) ? r : "…", null, null));
        }
    }

    private sealed class Store : IReviewStore
    {
        public ConcurrentDictionary<long, Review> Rows { get; } = new();

        public Task<long> InsertAsync(string repository, int pullRequestId, string title, string author, ProviderKey provider, string threadId, string? spaceId, CancellationToken cancellationToken)
        {
            var id = Rows.Count + 1;
            Rows[id] = new Review(id, repository, pullRequestId, title, author, provider, threadId, spaceId, ReviewStatus.Reviewing, null, null, null, null, null, null, null, []);
            return Task.FromResult((long)id);
        }

        public Task<Review?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(Rows.TryGetValue(id, out var r) ? r : null);

        public Task<Review?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.Values.FirstOrDefault(r => r.ThreadId == threadId));

        public Task SaveAsync(Review review, CancellationToken cancellationToken)
        {
            Rows[review.Id] = review;
            return Task.CompletedTask;
        }

        public Task AddMessageAsync(long reviewId, string direction, string author, string text, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<IdeaMessage>> ListMessagesAsync(long reviewId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IdeaMessage>>([]);

        public Task<IReadOnlyList<string>> ListOpenThreadsAsync(ProviderKey provider, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(Rows.Values.Where(r => r.Status != ReviewStatus.Closed).Select(r => r.ThreadId).ToList());
    }
}
