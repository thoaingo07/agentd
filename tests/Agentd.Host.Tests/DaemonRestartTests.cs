using Agentd.Application.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Host.Tests;

[TestClass]
public sealed class DaemonRestartTests
{
    [TestMethod]
    public async Task A_supervised_daemon_stops_once_shortly_after_the_request_and_audits_who_asked()
    {
        var lifetime = new Lifetime();
        var audit = new Audit();
        var restart = Restart(supervised: true, lifetime, audit);

        var first = await restart.RequestAsync("tngo", default);
        var again = await restart.RequestAsync("someone", default);
        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual((true, true, true), (first, again, restart.Requested));
        Assert.AreEqual(1, lifetime.Stops);
        Assert.AreEqual(("Daemon", "restart", "tngo"), (audit.Changes.Single().Key, audit.Changes.Single().Action, audit.Changes.Single().By));
    }

    [TestMethod]
    public async Task Without_a_supervisor_nothing_stops()
    {
        var lifetime = new Lifetime();
        var restart = Restart(supervised: false, lifetime, new Audit());

        Assert.IsFalse(await restart.RequestAsync("tngo", default));
        StringAssert.Contains(restart.Availability.Reason, "Agentd:Daemon:Supervised");
        await Task.Delay(DaemonRestart.Delay * 2);
        Assert.AreEqual((0, false), (lifetime.Stops, restart.Requested));
    }

    private static DaemonRestart Restart(bool supervised, Lifetime lifetime, Audit audit) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [DaemonRestart.SupervisedKey] = supervised ? "true" : null }).Build(),
            lifetime, audit, TimeProvider.System, NullLogger<DaemonRestart>.Instance);

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Stops { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            Stops++;
            Stopped.TrySetResult();
        }
    }

    private sealed class Audit : ISettingsAudit
    {
        public List<SettingsChange> Changes { get; } = [];

        public Task RecordAsync(IReadOnlyList<SettingsChange> changes, CancellationToken cancellationToken)
        {
            Changes.AddRange(changes);
            return Task.CompletedTask;
        }
    }
}
