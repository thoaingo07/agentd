using System.Text;
using Agentd.Application.AzureDevOps;
using Agentd.Application.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using static Agentd.Application.Tests.AzureDevOps.AdoConnectionsTests;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.AzureDevOps;

[TestClass]
public sealed class AdoOnBehalfTests
{
    private static readonly Guid s_dev = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly MemoryConnections _store = new();
    private readonly FakeDelegation _delegation = new();
    private readonly TestContext _t = new();

    [TestMethod]
    public async Task A_token_is_refreshed_once_rotated_and_cached_even_for_parallel_callers()
    {
        Connect();
        var tokens = Tokens();

        var all = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => tokens.GetAccessTokenAsync(s_dev, false, default)));
        var forced = await tokens.GetAccessTokenAsync(s_dev, forceRefresh: true, default);

        CollectionAssert.AreEqual(Enumerable.Repeat("access-1", 10).ToList(), all.ToList(), "one refresh for ten callers");
        Assert.AreEqual("access-2", forced);
        CollectionAssert.AreEqual(new[] { "refresh-1", "rotated-1" }, _delegation.Refreshed, "the rotated token is what's used next");
        Assert.AreEqual("enc:rotated-2", Encoding.UTF8.GetString(_store.Rows.Single().RefreshToken));
    }

    [TestMethod]
    public async Task A_refused_sign_in_is_marked_failed_and_an_unreachable_entra_isnt()
    {
        Connect();
        _delegation.RefreshFailure = new HttpRequestException("no route");
        var offline = await Tokens().GetAccessTokenAsync(s_dev, false, default);
        var stillConnected = _store.Rows.Single().Failed;

        _delegation.RefreshFailure = new InvalidOperationException("AADSTS700082: The refresh token has expired due to inactivity.");
        var refused = await Tokens().GetAccessTokenAsync(s_dev, false, default);

        Assert.AreEqual(((string?)null, false, (string?)null), (offline, stillConnected, refused));
        Assert.AreEqual((true, "AADSTS700082: The refresh token has expired due to inactivity."), (_store.Rows.Single().Failed, _store.Rows.Single().LastError));
        Assert.IsNull(await Tokens().GetAccessTokenAsync(s_dev, false, default), "failed stays failed until they reconnect");
    }

    [TestMethod]
    public async Task The_acting_identity_flows_into_awaited_calls_and_ends_with_its_scope()
    {
        var actor = new AdoActor();
        Guid? inside = null;

        using (actor.Begin(s_dev))
        {
            await Task.Run(async () =>
            {
                await Task.Yield();
                inside = actor.Current;
            });
            using (actor.Begin(null))
            {
                Assert.AreEqual(s_dev, actor.Current, "Begin(null) keeps whoever is acting");
            }
        }

        Assert.AreEqual(((Guid?)s_dev, (Guid?)null), (inside, actor.Current));
    }

    [TestMethod]
    public async Task A_jobs_pr_and_comments_go_under_the_assignee_when_connected()
    {
        Connect();
        UseOnBehalf();
        var request = await StartAsync();

        await _t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), default);

        Assert.AreEqual(s_dev, _t.PullRequests.CreatedAs.Single());
        Assert.IsTrue(_t.WorkItems.CommentedAs.All(a => a == s_dev), "the claim and the PR comment too");
        Assert.IsNull(_t.Actor!.Current, "the scope ended with the publish");
        Assert.IsFalse(_t.Outbox.Enqueued.Any(e => e.Message.Message.Markdown.Contains("🔗", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Without_a_connection_its_agentds_own_name_said_once_in_the_thread()
    {
        UseOnBehalf();
        var request = await StartAsync();

        await _t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), default);
        await _t.OnBehalf!.ResolveAsync(s_dev, "Dev One", request.JobId, default);

        Assert.IsNull(_t.PullRequests.CreatedAs.Single());
        var notes = _t.Outbox.Enqueued.Select(e => e.Message.Message.Markdown).Where(m => m.StartsWith("🔗", StringComparison.Ordinal)).ToList();
        Assert.HasCount(1, notes, "once per job");
        StringAssert.Contains(notes[0], "Dev One hasn't connected their Azure DevOps");
    }

    [TestMethod]
    public async Task Nobody_assigned_means_agentds_own_name_without_a_note()
    {
        UseOnBehalf(assigned: false);
        var request = await StartAsync();

        await _t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), default);

        Assert.IsNull(_t.PullRequests.CreatedAs.Single());
        Assert.IsFalse(_t.Outbox.Enqueued.Any(e => e.Message.Message.Markdown.StartsWith("🔗", StringComparison.Ordinal)));
    }

    /// <summary>Claims and starts the work item as set up (RunningJobAsync would replace the item and its assignee).</summary>
    private async Task<Ports.AgentRunRequest> StartAsync()
    {
        _ = await _t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(5617)), default);
        return (await _t.StartNext().Handle(new StartNextJob("w1"), default)).Value!;
    }

    private void Connect() => _store.Rows.Add(new(s_dev, "dev@example.com", "Dev One", "dev@example.com", Encoding.UTF8.GetBytes("enc:refresh-1"), false, null, default, default));

    private AdoUserTokens Tokens() => new(_store, new FakeProtector(), _delegation, _t.Clock);

    private void UseOnBehalf(bool assigned = true)
    {
        _t.Actor = new AdoActor();
        _t.OnBehalf = new AdoOnBehalf(_store, Tokens(), _t.Outbox);
        _t.PullRequests.ActingAs = () => _t.Actor.Current;
        _t.WorkItems.ActingAs = () => _t.Actor.Current;
        _t.WorkItems.Add(5617);
        if (assigned)
        {
            _t.WorkItems.Items[5617] = _t.WorkItems.Items[5617] with { AssignedToId = s_dev, AssignedTo = "Dev One" };
        }
    }
}
