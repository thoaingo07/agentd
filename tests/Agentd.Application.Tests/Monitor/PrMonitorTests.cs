using Agentd.Application.Messaging;
using Agentd.Application.Monitor;
using Agentd.Application.Ports;
using Agentd.Domain.Messaging;
using Microsoft.Extensions.Options;
using static Agentd.Application.Tests.AzureDevOps.AdoConnectionsTests;
using static Agentd.Application.Tests.Monitor.PrWatchServiceTests;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Monitor;

[TestClass]
public sealed class PrMonitorTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");
    private readonly TestContext _t = new();
    private readonly MemoryWatches _watches = new();
    private readonly MemoryConnections _connections = new();
    private readonly Search _search = new();
    private readonly Rounds _rounds;
    private readonly Fakes.FakeChat _chat = new("discord");

    public PrMonitorTests()
    {
        _rounds = new Rounds(_watches);
        _t.Chats.Add(_chat);
        _t.Messaging.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
        _t.PullRequests.Details[3944] = new PullRequestDetails(3944, "Deploy", null, "Dev", "feature/x", "develop", "h1", PullRequestStatus.Active, false, new Uri("https://x/pr/3944"));
        _connections.Rows.Add(new(Guid.NewGuid(), "kelvin@example.com", "Kelvin", "kelvin@example.com", [1], false, null, default, default));
        _watches.InsertAsync("sysmin", 3944, "Deploy", "tngo", s_discord, "t-1", null, [70], default).GetAwaiter().GetResult();
    }

    [TestMethod]
    public async Task A_failed_build_is_announced_then_after_five_quiet_minutes_a_round_is_prepared_with_its_errors()
    {
        _search.Latest = Build(901, "failed");

        await Monitor().PassAsync(default);
        await Monitor().PassAsync(default);   // still quiet time: nothing more
        _t.Clock.UtcNow += PrMonitor.Quiet;
        await Monitor().PassAsync(default);

        Assert.AreEqual("🔔 PR !3944: the PR build failed (run 901). I'll prepare a fix after 5 quiet minutes.", _chat.SentText.Single());
        var request = _rounds.Requests.Single();
        Assert.AreEqual((901, false, 0), (request.FailedBuild!.Build.Id, request.Conflicts, request.Comments.Count));
        var w = _watches.All.Single();
        Assert.AreEqual((901, 1, (DateTimeOffset?)null), (w.LastBuildId, w.FixRounds, w.SignalAt));
        Assert.IsNotNull(w.Pending, "the prepared round is kept");

        await Monitor().PassAsync(default);
        Assert.HasCount(1, _rounds.Requests, "nothing new while it waits for an answer, and the same build isn't handled twice");
    }

    [TestMethod]
    public async Task Only_new_open_comments_from_people_agentd_knows_and_conflicts_start_a_round()
    {
        _t.PullRequests.Details[3944] = _t.PullRequests.Details[3944] with { MergeStatus = "conflicts" };
        _t.PullRequests.Comments.AddRange([
            Comment(70, "kelvin@example.com", "active"),     // before watching: seen
            Comment(71, "kelvin@example.com", "active"),     // new and known: counts
            Comment(72, "stranger@example.com", "active"),   // not known: seen, ignored
            Comment(73, "kelvin@example.com", "closed"),     // resolved: ignored
        ]);

        await Monitor().PassAsync(default);
        _t.Clock.UtcNow += PrMonitor.Quiet;
        await Monitor().PassAsync(default);

        StringAssert.Contains(_chat.SentText.Single(), "merge conflicts, 1 new comment(s)");
        var request = _rounds.Requests.Single();
        Assert.AreEqual((true, 71), (request.Conflicts, request.Comments.Single().CommentId));
        CollectionAssert.AreEquivalent(new[] { 70, 71, 72, 73 }, _watches.All.Single().SeenComments.ToList());
    }

    [TestMethod]
    public async Task A_new_comment_during_the_quiet_time_waits_for_the_next_quiet_five_minutes()
    {
        _t.PullRequests.Comments.Add(Comment(71, "kelvin@example.com", "active"));
        await Monitor().PassAsync(default);
        _t.Clock.UtcNow += PrMonitor.Quiet - TimeSpan.FromMinutes(1);
        _t.PullRequests.Comments.Add(Comment(72, "kelvin@example.com", "active") with { PublishedAt = _t.Clock.UtcNow });
        _t.Clock.UtcNow += TimeSpan.FromMinutes(2);

        await Monitor().PassAsync(default);
        Assert.IsEmpty(_rounds.Requests, "the reviewer is still typing");
        _t.Clock.UtcNow += PrMonitor.Quiet;
        await Monitor().PassAsync(default);

        Assert.AreEqual(2, _rounds.Requests.Single().Comments.Count, "one review pass, one round");
    }

    [TestMethod]
    public async Task After_five_rounds_it_asks_once_to_take_over_and_a_finished_pr_stops()
    {
        var w = _watches.All.Single();
        await _watches.UpdateAsync(w.Id, PrMonitor.MaxRounds, w.SeenComments, null, null, null, default);
        _search.Latest = Build(902, "failed");

        await Monitor().PassAsync(default);
        await Monitor().PassAsync(default);
        _t.PullRequests.Details[3944] = _t.PullRequests.Details[3944] with { Status = PullRequestStatus.Completed };
        await Monitor().PassAsync(default);

        StringAssert.Contains(_chat.SentText[0], "I already prepared 5 fix rounds. Please take over");
        Assert.AreEqual("🏁 PR !3944 is completed: I stopped watching it.", _chat.SentText[1]);
        Assert.HasCount(2, _chat.SentText, "the take-over note isn't repeated");
        Assert.IsEmpty(_rounds.Requests);
        Assert.IsFalse(_watches.All.Single().Active);
    }

    private static PullRequestComment Comment(int id, string author, string status) =>
        new(7, id, author, $"comment {id}", "src/A.cs", 3, status, DateTimeOffset.UnixEpoch, author);

    private static BuildHit Build(int id, string result) => new(id, "sysmin-ci", "1", "completed", result, "refs/pull/3944/merge", null, "pullRequest", "h1", null, null);

    private PrMonitor Monitor() => new(_watches, _t.Registry, _t.PullRequests, _search, _connections,
        new MessagingProviderRegistry(_t.Chats, Options.Create(_t.Messaging)), _t.Clock, _rounds);

    private sealed class Rounds(MemoryWatches watches) : IPrFixRounds
    {
        public List<PrFixRequest> Requests { get; } = [];

        public async Task<bool> PrepareAsync(PrFixRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var w = request.Watch;
            await watches.UpdateAsync(w.Id, w.FixRounds, w.SeenComments, w.LastBuildId, null, new PendingFix("/wt/x", "c0ffee1", "fixed", [], DateTimeOffset.MaxValue), cancellationToken);
            return true;
        }
    }

    private sealed class Search : IAzureDevOpsSearch
    {
        public BuildHit? Latest { get; set; }

        public Task<IReadOnlyList<BuildHit>> ListBuildsAsync(string? pipeline, string? branch, string? result, int top, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BuildHit>>(Latest is null ? [] : [Latest]);

        public Task<BuildDetail?> GetBuildAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult<BuildDetail?>(new BuildDetail(Latest!, [new BuildFailure("dotnet test", "Task", "failed", ["exit code 1"], "Failed X")]));

        public Task<IReadOnlyList<WorkItemHit>> SearchWorkItemsAsync(WorkItemQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PullRequestHit>> ListPullRequestsAsync(string? repository, string status, int top, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineHit>> ListPipelinesAsync(string? name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<WikiHit>> SearchWikiAsync(string text, int top, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WikiPage?> GetWikiPageAsync(string? wiki, string? path, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
