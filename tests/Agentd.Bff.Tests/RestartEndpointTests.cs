using System.Net;
using System.Text.Json;
using Agentd.Application.Ideas;
using Agentd.Application.Ports;
using Agentd.Application.Setup;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class RestartEndpointTests
{
    [TestMethod]
    public async Task An_admin_restarts_a_supervised_agentd()
    {
        var daemon = new Daemon(available: true);
        await using var app = await StartAsync(daemon);
        var client = await new AntiforgeryClient(app).InitAsync();

        var status = JsonDocument.Parse(await app.GetTestClient().GetStringAsync(new Uri("/api/settings/restart", UriKind.Relative))).RootElement;
        using var noToken = await client.PostAsync("/api/settings/restart", token: "wrong");
        using var restart = await client.PostAsync("/api/settings/restart");

        Assert.AreEqual((true, JsonValueKind.Null), (status.GetProperty("available").GetBoolean(), status.GetProperty("reason").ValueKind));
        Assert.AreEqual((HttpStatusCode.BadRequest, HttpStatusCode.Accepted), (noToken.StatusCode, restart.StatusCode));
        CollectionAssert.AreEqual(new[] { "local" }, daemon.By, "once, by who asked");
    }

    [TestMethod]
    public async Task Without_a_supervisor_it_says_why_and_doesnt_stop()
    {
        var daemon = new Daemon(available: false);
        await using var app = await StartAsync(daemon);
        var client = await new AntiforgeryClient(app).InitAsync();

        var status = JsonDocument.Parse(await app.GetTestClient().GetStringAsync(new Uri("/api/settings/restart", UriKind.Relative))).RootElement;
        using var restart = await client.PostAsync("/api/settings/restart");

        Assert.AreEqual((false, "no supervisor"), (status.GetProperty("available").GetBoolean(), status.GetProperty("reason").GetString()));
        Assert.AreEqual(HttpStatusCode.Conflict, restart.StatusCode);
        StringAssert.Contains(await restart.Content.ReadAsStringAsync(), "no supervisor");
        Assert.IsEmpty(daemon.By);
    }

    [TestMethod]
    public async Task While_work_runs_it_lists_it_and_restarts_only_when_told_to_anyway()
    {
        var daemon = new Daemon(available: true);
        var turns = new ThreadTurns();
        using var turn = turns.Begin(ThreadTurnKind.Brainstorm, 4);
        var clock = new Clock();
        var job = Job.Create(WorkItemId.From(5702), RepositoryName.From("sysmin"), "t", clock);
        job.Persisted(new JobId(41), 1);
        job.BeginPreparing();
        await using var app = await StartAsync(daemon, new RestartCheck(new Jobs([job]), turns));
        var client = await new AntiforgeryClient(app).InitAsync();

        var status = JsonDocument.Parse(await app.GetTestClient().GetStringAsync(new Uri("/api/settings/restart", UriKind.Relative))).RootElement;
        using var refused = await client.PostAsync("/api/settings/restart");
        Assert.IsEmpty(daemon.By, "nothing stops while work runs");
        using var anyway = await client.PostAsync("/api/settings/restart?force=true");

        CollectionAssert.AreEqual(new[] { "Job 41 (WI-5702) is preparing", "Idea 4: the agent is answering" },
            status.GetProperty("running").EnumerateArray().Select(r => r.GetProperty("label").GetString()).ToArray());
        Assert.AreEqual(("job", 41L), (status.GetProperty("running")[0].GetProperty("source").GetString(), status.GetProperty("running")[0].GetProperty("id").GetInt64()));
        Assert.AreEqual((HttpStatusCode.Conflict, HttpStatusCode.Accepted), (refused.StatusCode, anyway.StatusCode));
        StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "Idea 4: the agent is answering");
        CollectionAssert.AreEqual(new[] { "local" }, daemon.By);
    }

    [TestMethod]
    public async Task With_nothing_running_it_restarts_without_asking_again()
    {
        var daemon = new Daemon(available: true);
        await using var app = await StartAsync(daemon, new RestartCheck(new Jobs([]), new ThreadTurns()));
        var client = await new AntiforgeryClient(app).InitAsync();

        var status = JsonDocument.Parse(await app.GetTestClient().GetStringAsync(new Uri("/api/settings/restart", UriKind.Relative))).RootElement;
        using var restart = await client.PostAsync("/api/settings/restart");

        Assert.AreEqual(0, status.GetProperty("running").GetArrayLength());
        Assert.AreEqual(HttpStatusCode.Accepted, restart.StatusCode);
    }

    private static async Task<WebApplication> StartAsync(IDaemonRestart daemon, RestartCheck? check = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton(daemon);
        if (check is not null)
        {
            builder.Services.AddSingleton(check);
        }

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private sealed class Daemon(bool available) : IDaemonRestart
    {
        public List<string> By { get; } = [];

        public RestartAvailability Availability => new(available, available ? null : "no supervisor");

        public Task<bool> RequestAsync(string by, CancellationToken cancellationToken)
        {
            if (available)
            {
                By.Add(by);
            }

            return Task.FromResult(available);
        }
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class Jobs(IReadOnlyList<Job> jobs) : IJobRepository
    {
        public Task<IReadOnlyList<Job>> ListByStateAsync(IReadOnlyCollection<JobState> states, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Job>>([.. jobs.Where(j => states.Contains(j.State))]);

        public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Job?> FindActiveByWorkItemAsync(WorkItemId workItem, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result> AddAsync(Job job, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result> SaveAsync(Job job, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Job?> DequeueNextAsync(string worker, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Job>> ListRecentAsync(TimeSpan window, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
