using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Agentd.Application.Events;
using Agentd.Application.Ports;
using Agentd.Bff.Http;
using Agentd.Bff.Hubs;
using Agentd.Bff.ViewModels;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class EventsHubTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task Replays_then_streams_live_without_gaps_or_duplicates()
    {
        await using var host = await HubHost.StartAsync();
        host.Append(7, 50);
        await using var client = await host.ConnectAsync();

        await client.SubscribeAsync("7", 0);
        await client.WaitForAsync(50);
        host.Append(7, 5);
        await client.WaitForAsync(55);

        CollectionAssert.AreEqual(Range(1, 55), client.Seqs("7"));
    }

    [TestMethod]
    public async Task Resubscribing_after_a_reconnect_resumes_after_the_last_seq()
    {
        await using var host = await HubHost.StartAsync();
        host.Append(7, 55);
        await using var client = await host.ConnectAsync();

        await client.SubscribeAsync("7", 30);
        await client.WaitForAsync(55);

        CollectionAssert.AreEqual(Range(31, 55), client.Seqs("7"));
    }

    [TestMethod]
    public async Task Subscribing_while_events_are_written_misses_nothing()
    {
        await using var host = await HubHost.StartAsync();
        host.Append(7, 20);
        using var writing = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            while (!writing.IsCancellationRequested)
            {
                host.Append(7, 1);
                await Task.Yield();
            }
        });
        await using var client = await host.ConnectAsync();

        await client.SubscribeAsync("7", 10);
        await Task.Delay(300);
        await writing.CancelAsync();
        await writer;
        await client.WaitForAsync(host.LastSeq);

        CollectionAssert.AreEqual(Range(11, host.LastSeq), client.Seqs("7"), "contiguous, in order, once");
    }

    [TestMethod]
    public async Task An_overflowing_live_buffer_resumes_from_the_store()
    {
        await using var host = await HubHost.StartAsync(overflowFirstSubscription: true);
        host.Append(7, 3);
        await using var client = await host.ConnectAsync();

        await client.SubscribeAsync("7", 0);
        await client.WaitForAsync(3);
        host.Append(7, 2);
        await client.WaitForAsync(5);

        CollectionAssert.AreEqual(Range(1, 5), client.Seqs("7"));
    }

    [TestMethod]
    public async Task The_all_stream_carries_summaries_and_big_payloads_are_trimmed()
    {
        await using var host = await HubHost.StartAsync();
        host.Append(7, 1, "agent.text");
        host.Append(7, 1, "phase.set");
        host.Append(7, 1, "agent.tool_result", new string('x', EventVm.MaxStreamedPayloadBytes + 1));
        await using var client = await host.ConnectAsync();

        await client.SubscribeAsync("all", 0);
        await client.SubscribeAsync("7", 0);
        await client.WaitForAsync(3, "7");

        CollectionAssert.AreEqual(new long[] { 2 }, client.Seqs("all"), "agent.* stays in the job stream");
        var big = client.Received.Single(r => r.Stream == "7" && r.Event.Seq == 3).Event.Payload;
        Assert.IsTrue(big.GetProperty("truncated").GetBoolean());
    }

    [TestMethod]
    public async Task The_hub_is_read_only_and_validates_streams()
    {
        await using var host = await HubHost.StartAsync();
        await using var client = await host.ConnectAsync();

        await Assert.ThrowsAsync<HubException>(() => client.Connection.InvokeAsync("CancelJob", 7L));
        var unknownJob = await Assert.ThrowsAsync<HubException>(() => client.SubscribeAsync("999", 0));
        StringAssert.Contains(unknownJob.Message, "Job 999 was not found.");
        await Assert.ThrowsAsync<HubException>(() => client.SubscribeAsync("jobs", 0));
    }

    [TestMethod]
    [DataRow("https://evil.example")]
    [DataRow(null)]
    public async Task Foreign_or_missing_origins_are_refused(string? origin)
    {
        await using var host = await HubHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/events/negotiate?negotiateVersion=1");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        using var response = await host.Server.CreateClient().SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task Disconnecting_stops_the_pumps()
    {
        await using var host = await HubHost.StartAsync();
        var client = await host.ConnectAsync();
        await client.SubscribeAsync("7", 0);
        await client.SubscribeAsync("all", 0);
        Assert.AreEqual(2, host.Streams.ActiveStreams);

        await client.DisposeAsync();

        var deadline = DateTime.UtcNow + s_timeout;
        while (host.Streams.ActiveStreams > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.AreEqual(0, host.Streams.ActiveStreams);
    }

    private static long[] Range(long from, long to) => Enumerable.Range((int)from, (int)(to - from + 1)).Select(i => (long)i).ToArray();

    private sealed class HubHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly Store _store;
        private readonly EventHub _live;

        private HubHost(WebApplication app, Store store, EventHub live)
        {
            _app = app;
            _store = store;
            _live = live;
        }

        public TestServer Server => _app.GetTestServer();

        public EventStreams Streams => _app.Services.GetRequiredService<EventStreams>();

        public long LastSeq => _store.LastSeq;

        public static async Task<HubHost> StartAsync(bool overflowFirstSubscription = false)
        {
            var store = new Store();
            var live = new EventHub(NullLogger<EventHub>.Instance);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddBff();
            builder.Services.AddSingleton<ILiveEvents>(overflowFirstSubscription ? new OverflowOnce(live) : live);
            builder.Services.AddSingleton<IEventReader>(store);
            builder.Services.AddSingleton<IJobRepository>(new Jobs());
            var app = builder.Build();
            app.UseBffHubOriginGuard();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapBff();
            await app.StartAsync();
            return new HubHost(app, store, live);
        }

        /// <summary>Commits <paramref name="count"/> events in seq order and publishes each, as the PostgreSQL listener does.</summary>
        public void Append(long jobId, int count, string type = "agent.text", string? text = null)
        {
            for (var i = 0; i < count; i++)
            {
                _live.Publish(_store.Add(jobId, type, text));
            }
        }

        public async Task<Client> ConnectAsync()
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(Server.BaseAddress, EventsHub.Path), o =>
                {
                    o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                    o.Headers["Origin"] = "http://localhost";
                })
                .Build();
            var client = new Client(connection);
            await connection.StartAsync();
            return client;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    private sealed class Client : IAsyncDisposable
    {
        public Client(HubConnection connection)
        {
            Connection = connection;
            connection.On<string, EventVm>(EventsHub.EventMethod, (stream, evt) => Received.Enqueue((stream, evt)));
        }

        public HubConnection Connection { get; }

        public ConcurrentQueue<(string Stream, EventVm Event)> Received { get; } = new();

        public Task SubscribeAsync(string stream, long afterSeq) => Connection.InvokeAsync("Subscribe", stream, afterSeq);

        public long[] Seqs(string stream) => Received.Where(r => r.Stream == stream).Select(r => r.Event.Seq).ToArray();

        public async Task WaitForAsync(long seq, string stream = "7")
        {
            var deadline = DateTime.UtcNow + s_timeout;
            while (!Received.Any(r => r.Stream == stream && r.Event.Seq >= seq))
            {
                Assert.IsLessThan(deadline, DateTime.UtcNow, $"seq {seq} never arrived on {stream}; got {string.Join(',', Seqs(stream))}");
                await Task.Delay(20);
            }

            await Task.Delay(100);   // anything extra (a duplicate) would arrive now
        }

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }

    /// <summary>The event table: seq order is commit order.</summary>
    private sealed class Store : IEventReader
    {
        private readonly List<AgentEventDto> _events = [];

        public long LastSeq
        {
            get
            {
                lock (_events)
                {
                    return _events.Count;
                }
            }
        }

        public AgentEventDto Add(long jobId, string type, string? text)
        {
            lock (_events)
            {
                var payload = JsonSerializer.SerializeToElement(new { text = text ?? "hi" });
                var evt = new AgentEventDto(_events.Count + 1, jobId, DateTimeOffset.UnixEpoch, type, payload);
                _events.Add(evt);
                return evt;
            }
        }

        public Task<IReadOnlyList<AgentEventDto>> ReadAfterAsync(JobId? jobId, long afterSeq, int limit, CancellationToken cancellationToken)
        {
            lock (_events)
            {
                return Task.FromResult<IReadOnlyList<AgentEventDto>>(_events
                    .Where(e => e.Seq > afterSeq && (jobId is { } id ? e.JobId == id.Value : e.IsSummary))
                    .Take(limit).ToList());
            }
        }

        public Task<IReadOnlyList<AgentEventDto>> ReadBeforeAsync(JobId jobId, long beforeSeq, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentEventDto?> GetAsync(long seq, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long> LatestSeqAsync(CancellationToken cancellationToken) => Task.FromResult(LastSeq);
    }

    /// <summary>The first live subscription overflows straight away, as a stalled client's would.</summary>
    private sealed class OverflowOnce(ILiveEvents inner) : ILiveEvents
    {
        private int _calls;

        public IAsyncEnumerable<AgentEventDto> Subscribe(long? jobId, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1 ? Overflow() : inner.Subscribe(jobId, cancellationToken);

#pragma warning disable CS1998 // an iterator that only throws
        private static async IAsyncEnumerable<AgentEventDto> Overflow()
        {
            throw new LiveEventsOverflowException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
#pragma warning restore CS1998
    }

    private sealed class Jobs : IJobRepository
    {
        public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken)
        {
            if (id.Value != 7)
            {
                return Task.FromResult<Job?>(null);
            }

            var job = Job.Create(WorkItemId.From(5613), RepositoryName.From("sysmin"), "Refine", new Clock());
            job.Persisted(id, 1);
            return Task.FromResult<Job?>(job);
        }

        public Task<Job?> FindActiveByWorkItemAsync(WorkItemId workItem, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result> AddAsync(Job job, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result> SaveAsync(Job job, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Job?> DequeueNextAsync(string worker, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Job>> ListByStateAsync(IReadOnlyCollection<JobState> states, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Job>> ListRecentAsync(TimeSpan window, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
