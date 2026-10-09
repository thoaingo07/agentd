using Agentd.Application.Ideas;
using Agentd.Application.Messaging;
using Agentd.Application.Reviews;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Agentd.Application.Tests.Reviews.ReviewSessionServiceTests;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Reviews;

[TestClass]
public sealed class BranchReviewsTests
{
    private static readonly ConversationRef s_channel = new(ProviderKey.From("discord"), "channel-1", null);
    private readonly TestContext _t = new();
    private readonly MemoryReviewSessions _store = new();

    [TestMethod]
    public async Task A_branch_review_from_chat_links_its_page_and_says_when_the_findings_are_in()
    {
        Branch("feature/keyset", head: "h1", baseTip: "r9", @base: "release/1.2");
        var (chat, reviewer) = Wire(new BrainstormReply(Findings, null, null));

        var reply = await chat.StartAsync(s_channel, "tngo", ["branch:feature/keyset", "--base", "release/1.2"], default);
        await reviewer.Start(1);

        Assert.AreEqual("🔍 Reviewing `feature/keyset` → `release/1.2` in sysmin (review #1). I'll say here when the findings are in. The page: https://agentd.example:18008/reviews/1", reply.Value);
        var notice = Sent().Single();
        StringAssert.StartsWith(notice, "📝 The review of `feature/keyset` → `release/1.2` is ready: 🔴 1 🟠 0. One real bug.");
        StringAssert.EndsWith(notice, "https://agentd.example:18008/reviews/1");
    }

    [TestMethod]
    public async Task A_failed_review_is_said_too_and_reviews_started_on_the_page_say_nothing()
    {
        Branch("feature/keyset", head: "h1", baseTip: "d9", @base: "develop");
        var (chat, reviewer) = Wire(new BrainstormReply("no block", null, null));

        await chat.StartAsync(s_channel, "tngo", ["branch:feature/keyset"], default);
        await reviewer.Start(1);
        var fromPage = (await new ReviewSessionService(_store, _t.Registry, _t.Worktrees, _t.PullRequests, null, reviewer)
            .StartAsync(new StartReview("sysmin", Branch: "feature/keyset"), "dev@example.com", default)).Value!;
        await reviewer.Start(fromPage.Id);

        StringAssert.StartsWith(Sent().Single(), "⚠️ The review of `feature/keyset` failed: The reviewer's findings couldn't be read");
    }

    [TestMethod]
    [DataRow(new[] { "branch:" }, "Which branch?")]
    [DataRow(new[] { "branch:x", "--focus", "security" }, "I don't know `--focus`")]
    public async Task Unclear_commands_say_how_to_write_them(string[] args, string expected)
    {
        var (chat, _) = Wire(new BrainstormReply(null, null, null));

        var reply = await chat.StartAsync(s_channel, "tngo", args, default);

        StringAssert.Contains(reply.Error!.Message, expected);
    }

    [TestMethod]
    public async Task With_several_repositories_it_asks_which()
    {
        _t.Registry.Repositories.Add(new Repository(RepositoryName.From("portal"), "git@x:v3/o/p/portal", new AzureDevOpsRepo("o", "p", "portal"), "develop", "repo:portal", []));
        var (chat, _) = Wire(new BrainstormReply(null, null, null));

        var reply = await chat.StartAsync(s_channel, "tngo", ["branch:x"], default);

        StringAssert.Contains(reply.Error!.Message, "add `--repo <name>` (sysmin, portal)");
        Assert.IsTrue(BranchReviews.Matches(["Branch:x"]));
        Assert.IsFalse(BranchReviews.Matches(["3944"]));
    }

    private const string Findings = """
        ```review-findings
        {"summary":"One real bug.","findings":[{"severity":"breaks","file":"src/Sync.cs","line":41,"title":"Unbounded read"}]}
        ```
        """;

    private void Branch(string name, string head, string baseTip, string @base)
    {
        _t.Worktrees.Commits[name] = head;
        _t.Worktrees.Commits[@base] = baseTip;
        _t.Worktrees.MergeBases[(baseTip, head)] = "m1";
    }

    private List<string> Sent() => _t.Chats.OfType<Fakes.FakeChat>().SelectMany(c => c.SentText).ToList();

    /// <summary>The reviewer finds the chat lazily (as in the daemon), so the service, reviewer and chat are wired through a container.</summary>
    private (BranchReviews Chat, ReviewSessionReviewer Reviewer) Wire(BrainstormReply reply)
    {
        _t.Chats.Add(new Fakes.FakeChat("discord"));
        _t.Messaging.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
        var services = new ServiceCollection()
            .AddSingleton<IReviewSessionStore>(_store)
            .AddSingleton<Ports.IRepositoryRegistry>(_t.Registry)
            .AddSingleton<Ports.IWorktreeManager>(_t.Worktrees)
            .AddSingleton<Ports.IPullRequestService>(_t.PullRequests)
            .AddSingleton<IBrainstormAgent>(new Agent(reply))
            .AddSingleton<IMessagingProviderRegistry>(new MessagingProviderRegistry(_t.Chats, Options.Create(_t.Messaging)))
            .AddSingleton(Options.Create(new WebLinkOptions { PublicOrigin = "https://agentd.example:18008/" }))
            .AddSingleton<ReviewSessionReviewer>()
            .AddSingleton<ReviewSessionService>()
            .AddSingleton<BranchReviews>()
            .BuildServiceProvider();
        return (services.GetRequiredService<BranchReviews>(), services.GetRequiredService<ReviewSessionReviewer>());
    }

    private sealed class Agent(BrainstormReply reply) : IBrainstormAgent
    {
        public Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken) => Task.FromResult(reply);
    }
}
