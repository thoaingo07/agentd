using System.Net.Sockets;
using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Host.Cli;
using Agentd.Host.Control;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Host.Tests.Control;

[TestClass]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]   // Unix sockets; each test also returns early on Windows
public sealed class ControlSocketTests : IAsyncDisposable
{
    // Short, so the socket path stays far below the Unix limit.
    private readonly ConfigHome _home = new ConfigHome(Directory.CreateTempSubdirectory("ag-ctl-").FullName).EnsureCreated();
    private readonly FakeJobs _jobs = new();
    private readonly ServiceProvider _services;
    private readonly List<ControlSocket> _sockets = [];

    public ControlSocketTests()
    {
        _services = new ServiceCollection()
            .AddSingleton<IQueryHandler<GetJobStatus, IReadOnlyList<JobStatusRow>>>(_jobs)
            .AddSingleton<ICommandHandler<ClaimWorkItem, JobId>>(_jobs)
            .AddSingleton<JobActivity>()
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton<IAgentRunner, IdleRunner>()
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(new SchedulerOptions()))
            .AddLogging()
            .AddSingleton<JobDispatcher>()
            .BuildServiceProvider();
    }

    private string SocketPath => ControlSocket.PathIn(_home);

    [TestMethod]
    public async Task The_socket_is_owner_only_answers_live_status_and_is_removed_on_stop()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix sockets only.");
        }

        var socket = await StartAsync();
        _services.GetRequiredService<JobActivity>().SetPhase(new JobId(7), "implement");
        _services.GetRequiredService<JobActivity>().RecordActivity(new JobId(7), "dotnet test", DateTimeOffset.UtcNow, running: true);

        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SocketPath));
        using var client = DaemonClient.TryCreate(_home)!;
        var jobs = await client.StatusAsync(all: false, CancellationToken.None);
        var job = jobs!.Single();
        Assert.AreEqual(("Running", "implement"), (job.State, job.Phase));
        Assert.StartsWith("dotnet test (running)", job.Activity);

        await socket.StopAsync(CancellationToken.None);
        Assert.IsFalse(File.Exists(SocketPath));
        Assert.IsNull(DaemonClient.TryCreate(_home), "no socket: the CLI uses the database");
    }

    [TestMethod]
    public async Task A_stale_socket_from_a_crash_is_replaced()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix sockets only.");
        }

        using (var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            dead.Bind(new UnixDomainSocketEndPoint(SocketPath));   // bound, never listening, then gone: what a crash leaves
        }

        await StartAsync();

        using var client = DaemonClient.TryCreate(_home)!;
        Assert.IsNotNull(await client.StatusAsync(all: false, CancellationToken.None));
    }

    [TestMethod]
    public async Task A_live_socket_is_left_to_the_daemon_that_owns_it()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix sockets only.");
        }

        await StartAsync();
        var second = await StartAsync();
        await second.StopAsync(CancellationToken.None);

        using var client = DaemonClient.TryCreate(_home)!;
        Assert.IsNotNull(await client.StatusAsync(all: false, CancellationToken.None), "the first daemon still answers");
    }

    [TestMethod]
    public async Task Run_through_the_daemon_claims_and_wakes_the_scheduler()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix sockets only.");
        }

        await StartAsync();
        using var client = DaemonClient.TryCreate(_home)!;

        var queued = await client.RunAsync(1234, "sysmin", CancellationToken.None);
        var refused = await client.RunAsync(1234, null, CancellationToken.None);

        Assert.AreEqual(42, queued!.Value.Run!.JobId);
        Assert.AreEqual(new ClaimWorkItem(WorkItemId.From(1234), Force: true, RepositoryName.From("sysmin")), _jobs.Claims[0]);
        Assert.AreEqual("conflict", refused!.Value.Error!.Code);
        var woken = _services.GetRequiredService<JobDispatcher>().WaitForWorkAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.AreSame(woken, await Task.WhenAny(woken, Task.Delay(TimeSpan.FromSeconds(5))), "signalled, not waiting for the next poll");
    }

    [TestMethod]
    public async Task Status_and_run_use_the_daemon_when_it_runs_and_the_database_when_it_doesnt()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix sockets only.");
        }

        var (offline, offlineOut, offlineErr) = await CliAsync("status");
        await StartAsync();
        var (online, onlineOut, onlineErr) = await CliAsync("status");
        var (_, runOut, _) = await CliAsync("run", "5613");

        Assert.AreEqual((ExitCodes.Ok, ExitCodes.Ok), (offline, online));
        Assert.Contains("(daemon not running; from the database)", offlineErr);
        Assert.DoesNotContain("PHASE", offlineOut);
        Assert.Contains("PHASE", onlineOut);
        Assert.AreEqual(string.Empty, onlineErr);
        Assert.Contains("the daemon is starting it", runOut);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var socket in _sockets)
        {
            await socket.DisposeAsync();
        }

        await _services.DisposeAsync();
        Directory.Delete(_home.Root, recursive: true);
    }

    private async Task<ControlSocket> StartAsync()
    {
        var socket = new ControlSocket(_home, _services, NullLogger<ControlSocket>.Instance);
        _sockets.Add(socket);
        await socket.StartAsync(CancellationToken.None);
        return socket;
    }

    private async Task<(int Exit, string Out, string Err)> CliAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        // The CLI disposes the services it's given; it gets its own (the database fallback), the daemon keeps _services.
        var cli = new ServiceCollection()
            .AddSingleton<IQueryHandler<GetJobStatus, IReadOnlyList<JobStatusRow>>>(_jobs)
            .AddSingleton<ICommandHandler<ClaimWorkItem, JobId>>(_jobs)
            .BuildServiceProvider();
        var exit = await AgentdCli.InvokeAsync(args, output, error, () => cli, (_, _) => Task.FromResult(0), CancellationToken.None, home: () => _home);
        return (exit, output.ToString(), error.ToString());
    }

    private sealed class FakeJobs : IQueryHandler<GetJobStatus, IReadOnlyList<JobStatusRow>>, ICommandHandler<ClaimWorkItem, JobId>
    {
        public List<ClaimWorkItem> Claims { get; } = [];

        public Task<IReadOnlyList<JobStatusRow>> Handle(GetJobStatus query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JobStatusRow>>([new(7, 5613, "sysmin", JobState.Running, TimeSpan.FromMinutes(12), 1, null, null)]);

        public Task<Result<JobId>> Handle(ClaimWorkItem command, CancellationToken cancellationToken)
        {
            Claims.Add(command);
            Result<JobId> result = Claims.Count == 1 || command.WorkItemId.Value == 5613 ? new JobId(42) : DomainError.Conflict("Work item 1234 already has active job 42.");
            return Task.FromResult(result);
        }
    }

    private sealed class IdleRunner : IAgentRunner
    {
        public Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public bool IsRunning(JobId jobId) => false;

        public void Cancel(JobId jobId)
        {
        }
    }
}
