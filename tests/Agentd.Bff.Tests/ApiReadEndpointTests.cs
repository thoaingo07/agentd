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
            new[] { "id", "workItemId", "title", "repo", "branch", "state", "phase", "startedAt", "elapsedSeconds", "prUrl", "waitingSince", "planStatus", "handoff", "fixRounds", "lastError", "completedAt", "pendingPermissions" },
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
    public async Task Config_lists_settings_and_never_secrets()
    {
        await using var app = await StartAsync();

        var json = await GetJsonAsync(app, "/api/config");

        Assert.AreEqual("ai-workflow", json.GetProperty("tag").GetString());
        Assert.AreEqual(60, json.GetProperty("pollIntervalSeconds").GetInt64());
        Assert.AreEqual("ermsystem", json.GetProperty("repositories")[0].GetProperty("organization").GetString());
        var names = new List<string>();
        Collect(json, names);
        foreach (var secret in new[] { "pat", "bottoken", "apikey", "clientsecret", "token", "password", "secret" })
        {
            Assert.IsFalse(names.Any(n => n.Contains(secret, StringComparison.OrdinalIgnoreCase)), $"'{secret}' in {string.Join(",", names)}");
        }

        // The view model itself has no such property, so no future config value can leak through it.
        var vmProperties = typeof(Agentd.Bff.ViewModels.ConfigVm).GetProperties().Select(p => p.Name)
            .Concat(typeof(Agentd.Bff.ViewModels.RepositoryVm).GetProperties().Select(p => p.Name));
        Assert.IsFalse(vmProperties.Any(n => n.Contains("Secret", StringComparison.Ordinal) || n.Contains("Token", StringComparison.Ordinal) || n.Contains("Key", StringComparison.Ordinal) || n == "Pat"));
    }

    private static void Collect(JsonElement e, List<string> names)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                names.Add(p.Name);
                Collect(p.Value, names);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                Collect(item, names);
            }
        }
    }

    [TestMethod]
    public async Task A_work_item_lists_its_jobs_prs_and_threads_and_its_conversation()
    {
        await using var app = await StartAsync();

        var wi = await GetJsonAsync(app, "/api/workitems/5613");
        var conversation = await GetJsonAsync(app, "/api/workitems/5613/conversation");
        using var missing = await app.GetTestClient().GetAsync(new Uri("/api/workitems/1", UriKind.Relative));
        using var both = await app.GetTestClient().GetAsync(new Uri("/api/workitems/5613/timeline?after=1&before=5", UriKind.Relative));

        Assert.AreEqual(7, wi.GetProperty("jobs")[0].GetProperty("id").GetInt64());
        Assert.AreEqual("https://dev.azure.com/ermsystem/Portal/_git/sysmin/pullrequest/3936", wi.GetProperty("pullRequests")[0].GetProperty("url").GetString());
        Assert.IsFalse(wi.GetProperty("conversations")[0].GetProperty("open").GetBoolean(), "a deleted thread is still listed");
        Assert.AreEqual("in", conversation[0].GetProperty("direction").GetString());
        Assert.AreEqual("tngo", conversation[0].GetProperty("author").GetString());
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [TestMethod]
    public async Task History_validates_states_and_paging()
    {
        await using var app = await StartAsync();

        var page = await GetJsonAsync(app, "/api/history?state=done,failed&repo=sysmin&q=AGENTS&from=2026-10-01&to=2026-10-03&page=2&pageSize=10");
        Assert.AreEqual("Done,Failed|sysmin|AGENTS|2|10|2026-10-01|2026-10-04", page.GetProperty("items")[0].GetProperty("title").GetString(), "filters reach the query");

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
            new HistoryPage([s_waiting with { Title = $"{string.Join(',', q.States!)}|{q.Repository}|{q.Text}|{q.Page}|{q.PageSize}|{q.From:yyyy-MM-dd}|{q.To:yyyy-MM-dd}" }], 11, false, q.Page, q.PageSize)));
        builder.Services.AddSingleton<IQueryHandler<GetJobEventDetail, AgentEventDto?>>(new Fixed<GetJobEventDetail, AgentEventDto?>(q => q.JobId.Value == 7 ? Event(q.Seq) : null));
        builder.Services.AddSingleton<ILiveEvents>(new EventHub(Microsoft.Extensions.Logging.Abstractions.NullLogger<EventHub>.Instance));
        builder.Services.AddSingleton<IQueryHandler<GetConfigSummary, ConfigSummary>>(new Fixed<GetConfigSummary, ConfigSummary>(_ =>
            new ConfigSummary("ai-workflow", "ai-in-progress", TimeSpan.FromMinutes(1), 2, true, true, true, [new RepositorySummary("sysmin", "ermsystem", "Portal", "develop")], ["discord"])));
        builder.Services.AddSingleton<IQueryHandler<GetWorkItem, WorkItemSummary?>>(new Fixed<GetWorkItem, WorkItemSummary?>(q => q.WorkItemId.Value != 5613 ? null :
            new WorkItemSummary(5613, "Refine the AGENTS.md", "sysmin", [s_waiting], [new PullRequestLink(7, "https://dev.azure.com/ermsystem/Portal/_git/sysmin/pullrequest/3936")],
                [new ConversationLink("discord", new Uri("https://discord.com/channels/1/2"), false)], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)));
        builder.Services.AddSingleton<IQueryHandler<GetWorkItemConversation, IReadOnlyList<ConversationEntry>>>(new Fixed<GetWorkItemConversation, IReadOnlyList<ConversationEntry>>(_ =>
            [new ConversationEntry(DateTimeOffset.UnixEpoch, 7, "in", "reply", "v2", "discord", "tngo", null, null)]));
        builder.Services.AddSingleton<IQueryHandler<GetWorkItemTimeline, EventPage>>(new Fixed<GetWorkItemTimeline, EventPage>(_ => new EventPage([], null, null, false)));
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
