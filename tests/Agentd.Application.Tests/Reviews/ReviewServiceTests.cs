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
          {"severity":"breaks","file":"charts/api/values.yaml","line":12,"title":"Readiness probe path is wrong","detail":"It points at /health.","suggestion":"Use /health/ready."},
          {"severity":"performance","file":"azure-pipelines.yml","line":40,"title":"Image tag isn't pinned"},
          {"severity":"breaks","title":"PR description is empty"}]}
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
        StringAssert.Contains(turn.Prompt, "WI-5617 \"Azure Pipelines: deploy to AKS\"");
        StringAssert.Contains(turn.Prompt, "- prod deploys on main", "the acceptance criteria, to check the PR against");
        StringAssert.Contains(turn.Prompt, "Kelvin Pham on charts/api/values.yaml:14: Why is the probe timeout 1s?", "open threads, so they aren't repeated");
        Assert.DoesNotContain("Typo fixed", turn.Prompt, "resolved threads are left out");
        Assert.AreEqual(("review-1", "abc1234"), h.Worktrees.CheckedOutCommits.Single(), "a checkout of the PR head");
        Assert.AreEqual(ReviewStatus.Reviewed, h.Store.Rows[1].Status);
        StringAssert.Contains(h.Chat.SentText.Last(), "**1.** 🔴 **Readiness probe path is wrong**");
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
        StringAssert.StartsWith(text, "🔴 **Readiness probe path is wrong**");
        Assert.HasCount(2, h.PullRequests.Threads, "the picked finding and the main message, nothing else");
        var main = h.PullRequests.Threads[1];
        Assert.AreEqual((null, (int?)null), (main.File, main.Line), "the main message is PR-wide");
        StringAssert.StartsWith(main.Text, "**Review** · 1 finding(s): 1 open, 0 fixed");
        StringAssert.Contains(main.Text, "Adds a deploy pipeline");
        StringAssert.Contains(main.Text, "| 🔴 | Readiness probe path is wrong | `charts/api/values.yaml:12` |");
        Assert.AreEqual(ReviewStatus.Posted, h.Store.Rows[1].Status);
        var stored = h.Store.Rows[1].Result!;
        Assert.AreEqual(ReviewFindings.Open, stored.Findings.Single().Status, "only the posted finding is followed");
        Assert.IsNotNull(stored.Findings.Single().Thread);
        Assert.IsNotNull(stored.MainThread);
        Assert.AreNotEqual(stored.MainThread, stored.Findings.Single().Thread);
        StringAssert.Contains(h.Chat.SentText[^2], "Posted 1 finding(s)");
        StringAssert.Contains(h.Chat.SentText.Last(), "re-check the findings after each push", "the thread stays open for re-checks");
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
        StringAssert.EndsWith(h.Worktrees.Removed.Single(), "/review-1", "the checkout goes with the thread; the conversation stays");
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

    [TestMethod]
    public async Task Review_again_rechecks_the_posted_findings_and_updates_the_pr()
    {
        var h = await PostedAsync();   // #1 and #2 posted, threads 101 and 102, main message 103
        h.PullRequests.Details[3944] = h.PullRequests.Details[3944] with { SourceCommit = "def5678aa" };   // a push
        h.PullRequests.Comments.Add(new PullRequestComment(102, 2, "Dev One", "Pinned it in the template.", "/azure-pipelines.yml", 40, "active", DateTimeOffset.UtcNow));
        h.Agent.Replies.Enqueue("""
            Checked the new commits.

            ```review-recheck
            {"findings":[{"n":1,"status":"fixed","reply":null},{"n":2,"status":"open","reply":"the tag is still `latest` in azure-pipelines.yml:44."}],
             "new":[{"severity":"breaks","file":"charts/api/deployment.yaml","line":7,"title":"Liveness probe now hits the DB","detail":"A slow DB restarts the pods.","suggestion":"Probe /health/live."}]}
            ```
            """);
        var sent = h.Chat.SentText.Count;

        Assert.IsTrue(await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "review again", default));
        await h.WaitForSentAsync(sent + 3);

        var prompt = h.Agent.Turns.Last().Prompt;
        StringAssert.Contains(prompt, "last checked at abc1234");
        StringAssert.Contains(prompt, "reply from Dev One: Pinned it in the template.", "the author's reply reaches the reviewer");
        Assert.AreEqual((101, "✅ Fixed in def5678."), (h.PullRequests.Replies[0].Thread, h.PullRequests.Replies[0].Text));
        Assert.AreEqual(PullRequestThreadStatus.Fixed, h.PullRequests.ThreadStatus[101], "a fixed finding's thread is resolved");
        Assert.AreEqual((102, "Still open in def5678: the tag is still `latest` in azure-pipelines.yml:44."), (h.PullRequests.Replies[1].Thread, h.PullRequests.Replies[1].Text));
        Assert.IsFalse(h.PullRequests.ThreadStatus.ContainsKey(102), "still active");
        StringAssert.StartsWith(h.PullRequests.Threads.Last().Text, "🔴 **Liveness probe now hits the DB**", "a new problem gets its thread");
        var main = h.PullRequests.Edited[103];
        StringAssert.StartsWith(main, "**Review** · 3 finding(s): 2 open, 1 fixed · checked at def5678");
        StringAssert.Contains(main, "| ✅ | Readiness probe path is wrong |");
        StringAssert.Contains(main, "| 🔴 | Liveness probe now hits the DB |");
        Assert.IsTrue(h.Chat.SentText.Any(t => t.Contains("re-checked at def5678:** 1 fixed ✅, 2 open, 1 new", StringComparison.Ordinal)));
        Assert.AreEqual("def5678aa", h.Store.Rows[1].HeadCommit);
    }

    [TestMethod]
    public async Task The_same_pr_again_continues_its_review()
    {
        var h = await PostedAsync();
        h.Agent.Replies.Enqueue("""
            ```review-recheck
            {"findings":[{"n":1,"status":"closed","reply":"Agreed: /health is the readiness path in this chart."},{"n":2,"status":"fixed"}]}
            ```
            """);

        var sent = h.Chat.SentText.Count;
        var started = await h.Service.StartAsync(s_discord, "tngo", ["3944"], default);
        await h.WaitForSentAsync(sent + 2);   // "Re-checking…" and the result line

        StringAssert.Contains(started.Value, "Continuing review #1");
        Assert.HasCount(1, h.Store.Rows, "no second review");
        Assert.AreEqual(PullRequestThreadStatus.Closed, h.PullRequests.ThreadStatus[101]);
        Assert.AreEqual("Agreed: /health is the readiness path in this chart.", h.PullRequests.Replies.First(r => r.Thread == 101).Text);
        StringAssert.Contains(h.PullRequests.Edited[103], "0 open, 1 fixed");
        Assert.IsTrue(h.Chat.SentText.Any(t => t.Contains("Everything is resolved", StringComparison.Ordinal)));
    }

    private const string AllFixed = """
        ```review-recheck
        {"findings":[{"n":1,"status":"fixed"},{"n":2,"status":"fixed"}]}
        ```
        """;

    [TestMethod]
    public async Task A_push_is_rechecked_once_without_being_asked()
    {
        var h = await PostedAsync();
        await h.Service.MonitorAsync(default);
        var turns = h.Agent.Turns.Count;
        Assert.AreEqual(turns, h.Agent.Turns.Count, "nothing new: no re-check");

        h.PullRequests.Details[3944] = h.PullRequests.Details[3944] with { SourceCommit = "def5678aa" };
        h.Agent.Replies.Enqueue(AllFixed);
        var sent = h.Chat.SentText.Count;
        await h.Service.MonitorAsync(default);
        await h.WaitForSentAsync(sent + 2);
        await h.Service.MonitorAsync(default);

        Assert.AreEqual(turns + 1, h.Agent.Turns.Count, "one push, one re-check");
        Assert.IsTrue(h.Chat.SentText.Any(t => t.Contains("Re-checking the posted findings (new commits)", StringComparison.Ordinal)));
        Assert.AreEqual(PullRequestThreadStatus.Fixed, h.PullRequests.ThreadStatus[102]);
    }

    [TestMethod]
    public async Task A_reply_on_a_findings_thread_is_rechecked_too()
    {
        var h = await PostedAsync();
        h.PullRequests.Comments.Add(new PullRequestComment(101, 2, "Dev One", "This path is right for our chart.", "/charts/api/values.yaml", 12, "active", DateTimeOffset.UtcNow));
        h.Agent.Replies.Enqueue(AllFixed);
        var sent = h.Chat.SentText.Count;

        await h.Service.MonitorAsync(default);
        await h.WaitForSentAsync(sent + 2);

        StringAssert.Contains(h.Agent.Turns.Last().Prompt, "This path is right for our chart.");
        Assert.IsTrue(h.Chat.SentText.Any(t => t.Contains("(new replies)", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task A_completed_pr_ends_the_review()
    {
        var h = await PostedAsync();
        h.PullRequests.Details[3944] = h.PullRequests.Details[3944] with { Status = PullRequestStatus.Completed };

        await h.Service.MonitorAsync(default);

        Assert.AreEqual(ReviewStatus.Closed, h.Store.Rows[1].Status);
        StringAssert.Contains(h.Chat.SentText.Last(), "PR !3944 is completed: this review is done.");
    }

    private static async Task<Harness> PostedAsync()
    {
        var h = new Harness();
        await h.ReviewedAsync();
        await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "post 1,2", default);
        Assert.AreEqual(ReviewStatus.Posted, h.Store.Rows[1].Status);
        return h;
    }

    [TestMethod]
    public async Task A_review_uses_the_review_steps_model_unless_it_names_its_own()
    {
        var jobs = new Agentd.Application.Jobs.JobOptions();
        jobs.Steps["review"] = new Agentd.Application.Jobs.StepModel { Model = "claude-opus-5-5", Effort = "High" };
        var defaults = new Harness(jobs);
        var own = new Harness(jobs);

        await defaults.Service.StartAsync(s_discord, "tngo", ["3944"], default);
        await own.Service.StartAsync(s_discord, "tngo", ["3944", "--model", "sonnet"], default);

        Assert.AreEqual(("claude-opus-5-5", "high"), (defaults.Store.Rows[1].Model, defaults.Store.Rows[1].Effort));
        Assert.AreEqual(("sonnet", "high"), (own.Store.Rows[1].Model, own.Store.Rows[1].Effort), "--model wins; the effort still defaults");
    }

    private sealed class Harness
    {
        public Harness(Agentd.Application.Jobs.JobOptions? jobs = null)
        {
            var options = Microsoft.Extensions.Options.Options.Create(new MessagingOptions());
            options.Value.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
            PullRequests.Details[3944] = new PullRequestDetails(3944, "Deploy to AKS", "Pipelines for test and prod.", "Dev One", "ai/5617-deploy", "develop", "abc1234",
                PullRequestStatus.Active, false, new Uri("https://dev.azure.com/ermsystem/Portal/_git/sysmin/pullrequest/3944"), [5617]);
            WorkItems.Items[5617] = new WorkItemDetails(5617, 3, "Azure Pipelines: deploy to AKS", "Active", @"Portal\sysmin", [], "Deploy all services.",
                "- test deploys on merge to develop\n- prod deploys on main", null, [], null);
            PullRequests.Comments.Add(new PullRequestComment(10, 1, "Kelvin Pham", "Why is the probe timeout 1s?", "/charts/api/values.yaml", 14, "active", DateTimeOffset.UnixEpoch));
            PullRequests.Comments.Add(new PullRequestComment(11, 2, "Kelvin Pham", "Typo fixed", null, null, "fixed", DateTimeOffset.UnixEpoch));
            Service = new ReviewService(Store, Registry, PullRequests, Worktrees, Agent, new MessagingProviderRegistry([Chat], options), NullLogger<ReviewService>.Instance, WorkItems,
                jobs is null ? null : Microsoft.Extensions.Options.Options.Create(jobs));
        }

        public FakeChat Chat { get; } = new("discord");

        public FakeRegistry Registry { get; } = new();

        public FakeWorktrees Worktrees { get; } = new();

        public FakePullRequests PullRequests { get; } = new();

        public FakeWorkItems WorkItems { get; } = new();

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

        public Task<IReadOnlyList<Review>> ListPostedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Review>>([.. Rows.Values.Where(r => r.Status == ReviewStatus.Posted).OrderBy(r => r.Id)]);

        public Task<Review?> FindLatestAsync(string repository, int pullRequestId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.Values.Where(r => r.Repository == repository && r.PullRequestId == pullRequestId).OrderByDescending(r => r.Id).FirstOrDefault());

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
