using Agentd.Application.Messaging;
using Agentd.Application.Monitor;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Domain.Repositories;
using Microsoft.Extensions.Options;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Monitor;

[TestClass]
public sealed class PrWatchServiceTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");
    private readonly TestContext _t = new();
    private readonly MemoryWatches _store = new();
    private readonly Fakes.FakeChat _chat = new("discord");

    public PrWatchServiceTests()
    {
        _t.Chats.Add(_chat);
        _t.Messaging.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
        _t.PullRequests.Details[3944] = new PullRequestDetails(3944, "Deploy", null, "Dev", "ai/5617-deploy", "develop", "h1", PullRequestStatus.Active, false, new Uri("https://x/pr/3944"));
        _t.PullRequests.Comments.Add(new PullRequestComment(7, 70, "Kelvin", "old comment", null, null, "active", DateTimeOffset.UnixEpoch));
    }

    [TestMethod]
    public async Task Watching_opens_a_thread_and_only_new_comments_will_count()
    {
        var reply = await Service().WatchAsync(s_discord, "tngo", ["!3944"], default);

        Assert.AreEqual("👀 Watching PR !3944 in its thread.", reply.Value);
        var opened = _chat.Opened.Single();
        StringAssert.Contains(opened.Opening.Markdown, "**Watching PR !3944**: Deploy");
        StringAssert.Contains(opened.Opening.Markdown, "ask here before pushing anything: **1** push · **2** discard");
        var w = _store.All.Single();
        Assert.AreEqual(("sysmin", 3944, "tngo"), (w.Repository, w.PullRequestId, w.WatchedBy));
        CollectionAssert.AreEqual(new[] { 70 }, w.SeenComments.ToList(), "comments before watching don't count");
    }

    [TestMethod]
    public async Task A_link_names_the_repository_and_a_second_watch_points_to_the_first()
    {
        _t.Registry.Repositories.Add(new Repository(RepositoryName.From("portal"), "git@x:v3/o/p/portal", new AzureDevOpsRepo("o", "p", "portal"), "develop", "repo:portal", []));

        var byNumber = await Service().WatchAsync(s_discord, "tngo", ["3944"], default);
        var byLink = await Service().WatchAsync(s_discord, "tngo", ["https://dev.azure.com/ermsystem/Portal/_git/sysmin/pullrequest/3944"], default);
        var again = await Service().WatchAsync(s_discord, "kelvin", ["!3944", "--repo", "sysmin"], default);

        StringAssert.Contains(byNumber.Error!.Message, "add `--repo <name>` (sysmin, portal)");
        Assert.AreEqual("👀 Watching PR !3944 in its thread.", byLink.Value);
        StringAssert.Contains(again.Value, "already watched");
        Assert.HasCount(1, _chat.Opened);
    }

    [TestMethod]
    public async Task A_finished_pr_isnt_watched_and_unwatch_stops_from_anywhere()
    {
        _t.PullRequests.Details[3945] = _t.PullRequests.Details[3944] with { Id = 3945, Status = PullRequestStatus.Completed };
        var done = await Service().WatchAsync(s_discord, "tngo", ["!3945"], default);
        await Service().WatchAsync(s_discord, "tngo", ["!3944"], default);

        var stopped = await Service().UnwatchAsync(["!3944"], default);
        var none = await Service().UnwatchAsync(["!3944"], default);

        StringAssert.Contains(done.Error!.Message, "completed");
        Assert.AreEqual("🛑 Stopped watching PR !3944.", stopped.Value);
        Assert.AreEqual("not_found", none.Error!.Code);
        Assert.AreEqual("🛑 Stopped watching PR !3944.", _chat.SentText.Last(), "said in the 👀 thread");
    }

    [TestMethod]
    public async Task In_the_thread_unwatch_stops_and_anything_else_gets_a_hint()
    {
        await Service().WatchAsync(s_discord, "tngo", ["!3944"], default);
        var watch = _store.All.Single();

        await Service().HandleThreadMessageAsync(watch, "what now?", default);
        await Service().HandleThreadMessageAsync(watch, "Unwatch.", default);

        StringAssert.Contains(_chat.SentText[0], "Say **unwatch** to stop");
        Assert.AreEqual("🛑 Stopped watching PR !3944.", _chat.SentText[1]);
        Assert.IsFalse(_store.All.Single().Active);
    }

    private PrWatchService Service() => new(_store, _t.Registry, _t.PullRequests, new MessagingProviderRegistry(_t.Chats, Options.Create(_t.Messaging)));

    /// <summary>Watches in memory, one active per PR like the routines.</summary>
    internal sealed class MemoryWatches : IPrWatchStore
    {
        public List<PrWatch> All { get; } = [];

        public Task<(long Id, bool Created)> InsertAsync(string repository, int pullRequestId, string title, string watchedBy, ProviderKey provider, string threadId, string? spaceId,
            IReadOnlyList<int> seenComments, CancellationToken cancellationToken)
        {
            if (All.FirstOrDefault(w => w.Active && w.Repository == repository && w.PullRequestId == pullRequestId) is { } existing)
            {
                return Task.FromResult((existing.Id, false));
            }

            var id = All.Count + 1L;
            All.Add(new PrWatch(id, repository, pullRequestId, title, watchedBy, provider, threadId, spaceId, true, 0, seenComments, null, null, null, DateTimeOffset.UnixEpoch));
            return Task.FromResult((id, true));
        }

        public Task<PrWatch?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(All.FirstOrDefault(w => w.Id == id));

        public Task<PrWatch?> FindActiveAsync(string repository, int pullRequestId, CancellationToken cancellationToken) =>
            Task.FromResult(All.FirstOrDefault(w => w.Active && w.Repository == repository && w.PullRequestId == pullRequestId));

        public Task<PrWatch?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) =>
            Task.FromResult(All.FirstOrDefault(w => w.Provider == provider && w.ThreadId == threadId));

        public Task<IReadOnlyList<PrWatch>> ListActiveAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PrWatch>>([.. All.Where(w => w.Active)]);

        public Task<bool> StopAsync(long id, CancellationToken cancellationToken)
        {
            var i = All.FindIndex(w => w.Id == id && w.Active);
            if (i < 0)
            {
                return Task.FromResult(false);
            }

            All[i] = All[i] with { Active = false, Pending = null };
            return Task.FromResult(true);
        }

        public Task UpdateAsync(long id, int fixRounds, IReadOnlyList<int> seenComments, int? lastBuildId, DateTimeOffset? signalAt, PendingFix? pending, CancellationToken cancellationToken)
        {
            var i = All.FindIndex(w => w.Id == id);
            All[i] = All[i] with { FixRounds = fixRounds, SeenComments = seenComments, LastBuildId = lastBuildId, SignalAt = signalAt, Pending = pending };
            return Task.CompletedTask;
        }
    }
}
