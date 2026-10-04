using System.Net;
using System.Text.Json;
using Agentd.Application.Abstractions;
using Agentd.Application.Events;
using Agentd.Application.Queries;
using Agentd.Domain.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class ApiReadEndpointTests
{
    private static readonly JobSummary s_waiting = new(7, 5613, "Refine the AGENTS.md", "sysmin", "ai/5613-refine", JobState.WaitingForHuman, "plan",
        DateTimeOffset.Parse("2026-10-03T16:35:00Z", System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromMinutes(12),
        null, DateTimeOffset.Parse("2026-10-03T16:40:00Z", System.Globalization.CultureInfo.InvariantCulture), PlanStatus.Pending, HandoffStatus.None, 0, null);

    [TestMethod]
    public async Task Dashboard_returns_the_view_model_shape()
    {
        await using var app = await StartAsync();

        var json = await GetJsonAsync(app, "/api/dashboard");

        Assert.AreEqual(1, json.GetProperty("stats").GetProperty("WaitingForHuman").GetInt32());
        Assert.AreEqual(99, json.GetProperty("latestSeq").GetInt64());
        var job = json.GetProperty("activeJobs")[0];
        CollectionAssert.AreEqual(
            new[] { "id", "workItemId", "title", "repo", "branch", "state", "phase", "startedAt", "elapsedSeconds", "prUrl", "waitingSince", "planStatus", "handoff", "fixRounds", "lastError" },
            job.EnumerateObject().Select(p => p.Name).ToArray(), "the TS contract");
        Assert.AreEqual("WaitingForHuman", job.GetProperty("state").GetString());
        Assert.AreEqual(720, job.GetProperty("elapsedSeconds").GetInt64());
    }

    [TestMethod]
    public async Task A_missing_job_is_a_404_problem()
    {
        await using var app = await StartAsync();

        using var response = await app.GetTestClient().GetAsync(new Uri("/api/jobs/999", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Job 999 was not found.");
    }

    [TestMethod]
    public async Task Event_pages_pass_the_paging_through_and_reject_both_directions()
    {
        await using var app = await StartAsync();

        var page = await GetJsonAsync(app, "/api/jobs/7/events?before=100&limit=2");
        Assert.AreEqual(98, page.GetProperty("events")[0].GetProperty("seq").GetInt64());
        Assert.IsTrue(page.GetProperty("hasMore").GetBoolean());
        Assert.AreEqual("phase.set", page.GetProperty("events")[0].GetProperty("type").GetString());
        Assert.AreEqual("plan", page.GetProperty("events")[0].GetProperty("payload").GetProperty("phase").GetString());

        using var both = await app.GetTestClient().GetAsync(new Uri("/api/jobs/7/events?after=1&before=9", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.BadRequest, both.StatusCode);
        using var tooMany = await app.GetTestClient().GetAsync(new Uri("/api/jobs/7/events?limit=501", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [TestMethod]
    public async Task A_single_event_is_served_in_full_for_its_job_only()
    {
        await using var app = await StartAsync();

        var found = await GetJsonAsync(app, "/api/jobs/7/events/42");
        using var otherJob = await app.GetTestClient().GetAsync(new Uri("/api/jobs/8/events/42", UriKind.Relative));

        Assert.AreEqual(42, found.GetProperty("seq").GetInt64());
        Assert.AreEqual(HttpStatusCode.NotFound, otherJob.StatusCode);
    }

    [TestMethod]
    public async Task History_validates_states_and_paging()
    {
        await using var app = await StartAsync();

        var page = await GetJsonAsync(app, "/api/history?state=done,failed&repo=sysmin&q=AGENTS&page=2&pageSize=10");
        Assert.AreEqual("Done,Failed|sysmin|AGENTS|2|10", page.GetProperty("items")[0].GetProperty("title").GetString(), "filters reach the query");

        using var bad = await app.GetTestClient().GetAsync(new Uri("/api/history?state=sleeping&pageSize=500", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
        var problem = JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("errors");
        Assert.IsTrue(problem.TryGetProperty("state", out _) && problem.TryGetProperty("pageSize", out _));
    }

    [TestMethod]
    public async Task Only_loopback_callers_are_the_local_user()
    {
        await using var app = await StartAsync(remote: IPAddress.Parse("10.1.2.3"));

        using var response = await app.GetTestClient().GetAsync(new Uri("/api/dashboard", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<WebApplication> StartAsync(IPAddress? remote = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton<IQueryHandler<GetDashboard, Dashboard>>(new Fixed<GetDashboard, Dashboard>(_ => new Dashboard(new Dictionary<JobState, int> { [JobState.WaitingForHuman] = 1 }, [s_waiting], 99)));
        builder.Services.AddSingleton<IQueryHandler<GetJob, JobDetail?>>(new Fixed<GetJob, JobDetail?>(q => q.JobId.Value == 7 ? new JobDetail(s_waiting, 1, 0, 0, null, null, null, null, []) : null));
        builder.Services.AddSingleton<IQueryHandler<GetJobEvents, EventPage>>(new Fixed<GetJobEvents, EventPage>(q =>
            new EventPage([Event(q.Before!.Value - 2), Event(q.Before.Value - 1)], q.Before.Value - 2, q.Before.Value - 1, true)));
        builder.Services.AddSingleton<IQueryHandler<SearchHistory, HistoryPage>>(new Fixed<SearchHistory, HistoryPage>(q =>
            new HistoryPage([s_waiting with { Title = $"{string.Join(',', q.States!)}|{q.Repository}|{q.Text}|{q.Page}|{q.PageSize}" }], 11, q.Page, q.PageSize)));
        builder.Services.AddSingleton<IQueryHandler<GetJobEventDetail, AgentEventDto?>>(new Fixed<GetJobEventDetail, AgentEventDto?>(q => q.JobId.Value == 7 ? Event(q.Seq) : null));
        builder.Services.AddSingleton<ILiveEvents>(new EventHub(Microsoft.Extensions.Logging.Abstractions.NullLogger<EventHub>.Instance));
        var app = builder.Build();
        if (remote is not null)
        {
            app.Use((ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = remote;
                return next(ctx);
            });
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private static AgentEventDto Event(long seq) =>
        new(seq, 7, DateTimeOffset.UnixEpoch, "phase.set", JsonDocument.Parse("""{"phase":"plan"}""").RootElement.Clone());

    private static async Task<JsonElement> GetJsonAsync(WebApplication app, string url)
    {
        using var response = await app.GetTestClient().GetAsync(new Uri(url, UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private sealed class Fixed<TQuery, TResult>(Func<TQuery, TResult> answer) : IQueryHandler<TQuery, TResult>
    {
        public Task<TResult> Handle(TQuery query, CancellationToken cancellationToken) => Task.FromResult(answer(query));
    }
}
