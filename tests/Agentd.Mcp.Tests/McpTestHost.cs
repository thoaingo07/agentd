using System.Collections.Concurrent;
using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Agentd.Mcp.Tests;

/// <summary>An in-memory host exposing /mcp exactly as the daemon does, with fake ports.</summary>
internal sealed class McpTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private McpTestHost(WebApplication app, FakeJobs jobs, FakeEvents events, FakeOutbox outbox)
    {
        _app = app;
        Jobs = jobs;
        Events = events;
        Outbox = outbox;
    }

    public FakeOutbox Outbox { get; }

    public FakeJobs Jobs { get; }

    public FakeEvents Events { get; }

    public IMcpTokenIssuer Tokens => _app.Services.GetRequiredService<IMcpTokenIssuer>();

    public HttpClient Http => _app.GetTestClient();

    public static async Task<McpTestHost> StartAsync()
    {
        var jobs = new FakeJobs();
        var events = new FakeEvents();
        var outbox = new FakeOutbox();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IClock>(new Clock());
        builder.Services.AddSingleton<IJobRepository>(jobs);
        builder.Services.AddSingleton<IEventStore>(events);
        builder.Services.AddSingleton<IWorkItemSource, WorkItems>();
        builder.Services.AddSingleton<ICommandHandler<PublishPullRequest, PullRequestRef>, Publish>();
        builder.Services.AddScoped<ICommandHandler<FinishWork, PullRequestRef>, FinishWorkHandler>();
        builder.Services.AddScoped<ICommandHandler<AskDeveloper, Unit>, AskDeveloperHandler>();
        builder.Services.AddScoped<ICommandHandler<ReportProgress, Unit>, ReportProgressHandler>();
        builder.Services.AddScoped<ICommandHandler<TakeDeveloperMessages, IReadOnlyList<string>>, TakeDeveloperMessagesHandler>();
        builder.Services.AddSingleton<JobActivity>();
        builder.Services.AddScoped<ICommandHandler<SetPhase, Unit>, SetPhaseHandler>();
        builder.Services.AddScoped<ICommandHandler<SubmitPlan, PlanOutcome>, SubmitPlanHandler>();
        builder.Services.AddScoped<ICommandHandler<ProposeKnowledge, Unit>, ProposeKnowledgeHandler>();
        builder.Services.AddSingleton<IOutbox>(outbox);
        builder.Services.AddAgentdMcp();

        var app = builder.Build();
        app.UseAgentdMcpOriginGuard();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAgentdMcp();
        await app.StartAsync();

        return new McpTestHost(app, jobs, events, outbox);
    }

    /// <summary>An MCP client authenticated with <paramref name="token"/>, like Claude Code with its mcp.json.</summary>
    public async Task<McpClient> ClientAsync(string token)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(_app.GetTestServer().BaseAddress, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
            },
            Http,
            ownsHttpClient: false);
        return await McpClient.CreateAsync(transport);
    }

    /// <summary>A job in Running state for work item <paramref name="workItem"/>.</summary>
    public Job RunningJob(long id, int workItem)
    {
        var clock = new Clock();
        var job = Job.Create(WorkItemId.From(workItem), RepositoryName.From("sysmin"), "Fix login", clock);
        job.Persisted(new JobId(id), 1);
        job.BeginPreparing();
        job.Start(new WorktreePath("/wt"), BranchName.From($"ai/{workItem}-fix"), ClaudeSessionId.New());
        job.DequeueEvents();
        Jobs.Rows[id] = job.ToSnapshot();
        return job;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    internal sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    internal sealed class FakeJobs : IJobRepository
    {
        public ConcurrentDictionary<long, JobSnapshot> Rows { get; } = new();

        public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.TryGetValue(id.Value, out var s) ? Job.Rehydrate(s, new Clock()) : null);

        public Task<Result> SaveAsync(Job job, CancellationToken cancellationToken)
        {
            job.DequeueEvents();
            job.Persisted(job.Id, job.Version + 1);
            Rows[job.Id.Value] = job.ToSnapshot();
            return Task.FromResult(Result.Ok);
        }

        public Task<Job?> FindActiveByWorkItemAsync(WorkItemId workItem, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result> AddAsync(Job job, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Job?> DequeueNextAsync(string worker, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Job>> ListByStateAsync(IReadOnlyCollection<JobState> states, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Job>> ListRecentAsync(TimeSpan window, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    internal sealed class FakeEvents : IEventStore
    {
        public ConcurrentQueue<(long? JobId, string Type, string Payload)> Appended { get; } = new();

        public Task<long> AppendAsync(JobId? jobId, string type, string payloadJson, CancellationToken cancellationToken)
        {
            Appended.Enqueue((jobId?.Value, type, payloadJson));
            return Task.FromResult((long)Appended.Count);
        }
    }

    internal sealed class FakeOutbox : IOutbox
    {
        public ConcurrentQueue<(long JobId, OutboxMessage Message)> Enqueued { get; } = new();

        public Task EnqueueAsync(JobId jobId, IReadOnlyList<OutboxMessage> messages, CancellationToken cancellationToken)
        {
            foreach (var message in messages)
            {
                Enqueued.Enqueue((jobId.Value, message));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class WorkItems : IWorkItemSource
    {
        public Task<WorkItemDetails?> GetAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult<WorkItemDetails?>(new(id, 1, "Fix login", "Active", "Portal", [], "The redirect loops.", "Redirects once.", null, [], null));

        public Task<IReadOnlyList<WorkItemRef>> QueryTaggedAsync(string tag, string excludeTag, IReadOnlyList<string> states, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryClaimAsync(int id, int rev, string claimTag, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddCommentAsync(int id, string text, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Stands in for push + PR creation: completes the job with a fixed PR URL.</summary>
    private sealed class Publish(IJobRepository jobs) : ICommandHandler<PublishPullRequest, PullRequestRef>
    {
        public async Task<Result<PullRequestRef>> Handle(PublishPullRequest command, CancellationToken cancellationToken)
        {
            var job = (await jobs.GetAsync(command.JobId, cancellationToken))!;
            var pr = new PullRequestRef(7, new Uri($"https://dev.azure.com/ermsystem/Portal/_git/sysmin/pullrequest/{command.JobId}"));
            job.Complete(new PullRequestUrl(pr.Url));
            await jobs.SaveAsync(job, cancellationToken);
            return pr;
        }
    }
}
