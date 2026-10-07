using Agentd.Application.Setup;

namespace Agentd.Application.Tests.Setup;

[TestClass]
public sealed class SetupServiceTests
{
    private const string ConnectionString = "Host=db;Username=agentd;Password=s3cret-pw;Database=agentd";

    private readonly FakeSecrets _secrets = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeDatabase _database = new();

    [TestMethod]
    public async Task Saving_the_database_stores_a_secret_and_audits_it_without_the_value()
    {
        var result = await Service().SaveDatabaseAsync(ConnectionString, "setup", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Value!.RestartRequired);
        Assert.AreEqual(ConnectionString, _secrets.Values[SetupService.ConnectionStringSecret]);
        var change = _audit.Changes.Single();
        Assert.AreEqual((SetupService.ConnectionStringSecret, "secret", "set", "setup"), (change.Key, change.Kind, change.Action, change.By));
        Assert.IsTrue(Service().GetDatabase().ConnectionString.Set);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("nonsense")]
    public async Task An_invalid_connection_string_is_refused_and_nothing_is_stored(string value)
    {
        var result = await Service().SaveDatabaseAsync(value, "setup", CancellationToken.None);

        Assert.AreEqual("validation", result.Error?.Code);
        Assert.IsEmpty(_secrets.Values);
        Assert.IsEmpty(_audit.Changes);
    }

    [TestMethod]
    public async Task Test_uses_the_given_connection_string_or_else_the_saved_one()
    {
        var none = await Service().TestDatabaseAsync(null, CancellationToken.None);
        await Service().TestDatabaseAsync("Host=other", CancellationToken.None);
        _secrets.Values[SetupService.ConnectionStringSecret] = ConnectionString;
        await Service().TestDatabaseAsync(" ", CancellationToken.None);

        Assert.IsFalse(none.Ok);
        CollectionAssert.AreEqual(new[] { "Host=other", ConnectionString }, _database.Tested);
    }

    [TestMethod]
    public async Task Migrate_needs_a_saved_connection_string_and_is_audited()
    {
        var before = await Service().MigrateDatabaseAsync("setup", CancellationToken.None);
        _secrets.Values[SetupService.ConnectionStringSecret] = ConnectionString;
        var after = await Service().MigrateDatabaseAsync("setup", CancellationToken.None);

        Assert.IsFalse(before.Ok);
        Assert.IsTrue(after.Ok);
        CollectionAssert.AreEqual(new[] { ConnectionString }, _database.Migrated);
        Assert.AreEqual("migrated", _audit.Changes.Single().Action);
    }

    private SetupService Service() => new(_secrets, _audit, _database, TimeProvider.System);

    private sealed class FakeSecrets : ISecrets
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public SecretStatus Status(string key) => Values.ContainsKey(key) ? new SecretStatus(true, DateTimeOffset.UnixEpoch, "setup") : SecretStatus.Missing;

        public string? TryGet(string key) => Values.GetValueOrDefault(key);

        public void Store(string key, string value, string by) => Values[key] = value;

        public bool Remove(string key) => Values.Remove(key);
    }

    private sealed class FakeAudit : ISettingsAudit
    {
        public List<SettingsChange> Changes { get; } = [];

        public Task RecordAsync(IReadOnlyList<SettingsChange> changes, CancellationToken cancellationToken)
        {
            Changes.AddRange(changes);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDatabase : IDatabaseProbe
    {
        public List<string> Tested { get; } = [];

        public List<string> Migrated { get; } = [];

        public string? Problem(string connectionString) => connectionString.Contains('=', StringComparison.Ordinal) ? null : "not a connection string";

        public Task<StepCheck> TestAsync(string connectionString, CancellationToken cancellationToken)
        {
            Tested.Add(connectionString);
            return Task.FromResult(new StepCheck(true, "connected"));
        }

        public Task<StepCheck> MigrateAsync(string connectionString, CancellationToken cancellationToken)
        {
            Migrated.Add(connectionString);
            return Task.FromResult(new StepCheck(true, "migrated"));
        }
    }
}
