using Agentd.Application.Incidents;
using Agentd.Application.Setup;

namespace Agentd.Application.Tests.Incidents;

[TestClass]
public sealed class MonitorSettingsTests
{
    private const string OrdersId = "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Insights/components/orders-ai";
    private const string BillingId = "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Insights/components/billing-ai";
    private readonly Config _config = new();
    private readonly Audit _audit = new();
    private readonly FakeTelemetry _telemetry = new();

    [TestMethod]
    public async Task Saving_writes_the_section_to_agentd_json_audited_and_needs_a_restart_once()
    {
        var input = new MonitorInput(true, " tenant-1 ", [App("orders-api", OrdersId, " sysmin ", "orders"), App("billing", BillingId, "billing")]);

        var saved = await Settings().SaveAsync(input, "tngo", default);
        var again = await Settings().SaveAsync(input, "tngo", default);

        Assert.AreEqual((true, false), (saved.Value!.RestartRequired, again.Value!.RestartRequired), "nothing changed the second time");
        var step = Settings().Get();
        Assert.AreEqual((true, "tenant-1", 2), (step.Enabled, step.TenantId, step.Apps.Count));
        Assert.AreEqual(("orders-api", OrdersId, "sysmin", "orders"), (step.Apps[0].Name, step.Apps[0].ResourceId, step.Apps[0].Repository, step.Apps[0].RoleName));
        Assert.IsNull(step.Apps[1].RoleName);
        CollectionAssert.AreEqual(new[] { "Monitor:Enabled", "Monitor:TenantId", "Monitor:Apps:0", "Monitor:Apps:1" }, _audit.Changes.Select(c => c.Key).ToArray());
    }

    [TestMethod]
    public async Task Removing_an_app_drops_its_slot_and_turning_it_off_keeps_the_list()
    {
        await Settings().SaveAsync(new MonitorInput(true, null, [App("orders-api", OrdersId, "sysmin"), App("billing", BillingId, "billing")]), "tngo", default);
        _audit.Changes.Clear();

        await Settings().SaveAsync(new MonitorInput(false, null, [App("orders-api", OrdersId, "sysmin")]), "kelvin", default);

        var step = Settings().Get();
        Assert.AreEqual((false, 1), (step.Enabled, step.Apps.Count));
        CollectionAssert.AreEqual(new[] { "Monitor:Enabled set", "Monitor:Apps:1 removed" }, _audit.Changes.Select(c => $"{c.Key} {c.Action}").ToArray());
    }

    [TestMethod]
    public async Task What_startup_would_refuse_is_refused_here_and_off_with_no_apps_is_fine()
    {
        var noApps = await Settings().SaveAsync(new MonitorInput(true, null, []), "tngo", default);
        var badId = await Settings().SaveAsync(new MonitorInput(false, null, [App("orders-api", "/subscriptions/s1", "sysmin")]), "tngo", default);
        var twice = await Settings().SaveAsync(new MonitorInput(true, null, [App("orders", OrdersId, "sysmin"), App("ORDERS", BillingId, "billing")]), "tngo", default);
        var off = await Settings().SaveAsync(new MonitorInput(false, null, []), "tngo", default);

        StringAssert.Contains(noApps.Error!.Message, "list at least one");
        StringAssert.Contains(badId.Error!.Message, "ResourceId");
        StringAssert.Contains(twice.Error!.Message, "listed twice");
        Assert.IsTrue(off.IsSuccess);
        Assert.IsFalse(off.Value!.RestartRequired, "off was off already");
        Assert.IsEmpty(_audit.Changes, "nothing refused was written");
    }

    [TestMethod]
    public async Task An_app_list_set_below_agentd_json_can_not_be_shortened()
    {
        _config.Below["Monitor:Apps:0:Name"] = "appsettings.Development.json";

        var result = await Settings().SaveAsync(new MonitorInput(false, null, []), "tngo", default);

        StringAssert.Contains(result.Error!.Message, "appsettings.Development.json");
        Assert.IsEmpty(_audit.Changes);
    }

    [TestMethod]
    public async Task Test_connection_reads_the_last_hour_and_explains_a_refusal()
    {
        _telemetry.Groups["orders-api"] = [new ErrorGroup("ex|1|GET", ErrorKinds.Exception, "NullReferenceException", null, "GET /orders", null, 7, 2,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new ErrorSample(null, null, null))];
        _telemetry.Failing.Add("billing");

        var ok = await Settings().TestAsync(App("orders-api", OrdersId, "sysmin"), default);
        var refused = await Settings().TestAsync(App("billing", BillingId, "billing"), default);
        var invalid = await Settings().TestAsync(App("orders-api", "nope", "sysmin"), default);

        Assert.AreEqual((true, "Read orders-api: 1 error group(s), 7 occurrence(s) in the last hour."), (ok.Ok, ok.Message));
        Assert.AreEqual(TimeSpan.FromHours(1), _telemetry.Windows.Single().Until - _telemetry.Windows.Single().Since);
        Assert.IsFalse(refused.Ok);
        StringAssert.Contains(refused.Message, "AuthorizationFailed");
        StringAssert.Contains(refused.Fix, "Reader");
        Assert.IsFalse(invalid.Ok);
        Assert.HasCount(1, _telemetry.Windows, "an invalid app isn't queried");
    }

    private MonitorSettings Settings() => new(_config, _audit, _telemetry, TimeProvider.System);

    private static MonitoredApp App(string name, string id, string repo, string? role = null) => new() { Name = name, ResourceId = id, Repository = repo, RoleName = role };

    /// <summary>agentd.json as flat keys; setting a section to null removes everything under it.</summary>
    private sealed class Config : IConfigWriter
    {
        public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Below { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Read(string key) => Values.GetValueOrDefault(key);

        public IReadOnlyList<string> Children(string section) => [];

        public string? SetBelow(string key) => Below.GetValueOrDefault(key);

        public Task SetAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken)
        {
            foreach (var (k, v) in values)
            {
                if (v is null)
                {
                    foreach (var under in Values.Keys.Where(x => x.Equals(k, StringComparison.OrdinalIgnoreCase) || x.StartsWith($"{k}:", StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        Values.Remove(under);
                    }
                }
                else
                {
                    Values[k] = v;
                }
            }

            return Task.CompletedTask;
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
