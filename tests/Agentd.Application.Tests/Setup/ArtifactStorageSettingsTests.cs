using Agentd.Application.Artifacts;
using Agentd.Application.Setup;

namespace Agentd.Application.Tests.Setup;

[TestClass]
public sealed class ArtifactStorageSettingsTests
{
    private const string Url = "https://acct.r2.cloudflarestorage.com/agentd";
    private readonly Config _config = new();
    private readonly Secrets _secrets = new();
    private readonly Audit _audit = new();
    private readonly Probe _probe = new();

    [TestMethod]
    public async Task Saving_writes_the_urls_to_agentd_json_and_the_keys_to_the_secret_store_audited_without_values()
    {
        var saved = await Settings().SaveAsync(new(" " + Url + "/ ", "https://agentd.thoaingo.com/", "key-id", "key-secret"), "tngo", default);

        Assert.IsTrue(saved.Value!.RestartRequired);
        Assert.AreEqual((Url, "https://agentd.thoaingo.com"), (_config.Values[ArtifactStorageSettings.UrlKey], _config.Values[ArtifactStorageSettings.PublicUrlKey]));
        Assert.AreEqual(("key-id", "key-secret"), (_secrets.Values[ArtifactStorageSettings.AccessKeyIdSecret], _secrets.Values[ArtifactStorageSettings.SecretAccessKeySecret]));
        CollectionAssert.AreEqual(new[] { "Artifacts:S3:Url set", "Artifacts:S3:PublicUrl set", "Artifacts:S3:AccessKeyId set", "Artifacts:S3:SecretAccessKey set" },
            _audit.Changes.Select(c => $"{c.Key} {c.Action}").ToArray());
        Assert.IsTrue(_audit.Changes.All(c => c.By == "tngo"));
    }

    [TestMethod]
    public async Task Empty_keys_keep_the_stored_ones_and_an_empty_url_goes_back_to_the_database()
    {
        _secrets.Values[ArtifactStorageSettings.AccessKeyIdSecret] = "old-id";
        _secrets.Values[ArtifactStorageSettings.SecretAccessKeySecret] = "old-secret";
        _config.Values[ArtifactStorageSettings.UrlKey] = Url;

        await Settings().SaveAsync(new(Url, null, "", null), "tngo", default);
        Assert.AreEqual("old-id", _secrets.Values[ArtifactStorageSettings.AccessKeyIdSecret]);
        Assert.IsEmpty(_audit.Changes, "nothing changed");

        await Settings().SaveAsync(new("", null, null, null), "tngo", default);
        Assert.IsNull(_config.Values[ArtifactStorageSettings.UrlKey]);
        Assert.AreEqual("Artifacts:S3:Url removed", $"{_audit.Changes.Single().Key} {_audit.Changes.Single().Action}");
    }

    [TestMethod]
    public async Task The_folder_for_pages_without_s3_is_shown_saved_reset_and_checked()
    {
        _config.Values[ArtifactStorageSettings.FolderKey] = "/home/agentd/.agentd/artifacts";
        Assert.AreEqual("/home/agentd/.agentd/artifacts", Settings().Get().Folder);

        await Settings().SaveAsync(new(null, null, null, null, "~/pages"), "tngo", default);
        Assert.AreEqual("~/pages", _config.Values[ArtifactStorageSettings.FolderKey]);
        await Settings().SaveAsync(new(null, null, null, null), "tngo", default);
        Assert.AreEqual("~/pages", _config.Values[ArtifactStorageSettings.FolderKey], "not sent: kept");
        await Settings().SaveAsync(new(null, null, null, null, " "), "tngo", default);
        Assert.IsNull(_config.Values[ArtifactStorageSettings.FolderKey], "empty: back to the default in agentd's home");

        var bad = await Settings().SaveAsync(new(null, null, null, null, "../etc"), "tngo", default);
        StringAssert.Contains(bad.Error!.Message, "full path");
        Assert.IsTrue(_audit.Changes.Any(c => c.Key == ArtifactStorageSettings.FolderKey && c.Action == "set"));
    }

    [TestMethod]
    [DataRow("https://acct.r2.cloudflarestorage.com", null, "ends with the bucket")]
    [DataRow("https://acct.r2.cloudflarestorage.com/agentd/pages", null, "ends with the bucket")]
    [DataRow("ftp://acct/agentd", null, "ends with the bucket")]
    [DataRow(Url, "agentd.thoaingo.com", "web address")]
    [DataRow(Url, "https://agentd.thoaingo.com/?x=1", "web address")]
    public async Task Bad_urls_are_refused_and_nothing_is_written(string url, string? publicUrl, string message)
    {
        var saved = await Settings().SaveAsync(new(url, publicUrl, "id", "secret"), "tngo", default);

        StringAssert.Contains(saved.Error!.Message, message);
        Assert.IsEmpty(_config.Values);
        Assert.IsEmpty(_secrets.Values);
    }

    [TestMethod]
    public async Task A_url_needs_both_keys_given_or_stored()
    {
        var saved = await Settings().SaveAsync(new(Url, null, "id", null), "tngo", default);

        StringAssert.Contains(saved.Error!.Message, "access key ID and secret access key");
    }

    [TestMethod]
    public async Task Testing_uses_the_given_settings_with_the_stored_keys_and_saves_nothing()
    {
        _secrets.Values[ArtifactStorageSettings.AccessKeyIdSecret] = "old-id";
        _secrets.Values[ArtifactStorageSettings.SecretAccessKeySecret] = "old-secret";

        var check = await Settings().TestAsync(new(Url, "https://agentd.thoaingo.com", null, "new-secret"), default);
        var none = await Settings().TestAsync(new(null, null, null, null), default);

        Assert.IsTrue(check.Ok);
        Assert.AreEqual((Url, "https://agentd.thoaingo.com", "old-id", "new-secret"), (_probe.Tried!.Url, _probe.Tried.PublicUrl, _probe.Tried.AccessKeyId, _probe.Tried.SecretAccessKey));
        Assert.AreEqual((false, 1), (none.Ok, _probe.Calls));
        Assert.IsEmpty(_config.Values);
        Assert.AreEqual("old-secret", _secrets.Values[ArtifactStorageSettings.SecretAccessKeySecret]);
    }

    [TestMethod]
    public void It_shows_what_is_set_and_what_overrides_it()
    {
        _config.Values[ArtifactStorageSettings.UrlKey] = Url;
        _config.Overrides[ArtifactStorageSettings.PublicUrlKey] = "an environment variable or the command line";
        _secrets.Values[ArtifactStorageSettings.AccessKeyIdSecret] = "id";

        var step = Settings().Get();

        Assert.AreEqual((Url, (string?)null, (string?)null, "an environment variable or the command line"), (step.Url, step.PublicUrl, step.UrlOverriddenBy, step.PublicUrlOverriddenBy));
        Assert.AreEqual((true, false), (step.AccessKeyId.Set, step.SecretAccessKey.Set));
        Assert.DoesNotContain("secret", new ArtifactStorageInput(Url, null, "id", "secret").ToString(), "keys never print");
    }

    private ArtifactStorageSettings Settings() => new(_config, _secrets, _audit, _probe, TimeProvider.System);

    private sealed class Config : IConfigWriter
    {
        public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Read(string key) => Values.GetValueOrDefault(key);

        public IReadOnlyList<string> Children(string section) => [];

        public string? OverriddenBy(string key) => Overrides.GetValueOrDefault(key);

        public Task SetAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken)
        {
            foreach (var (k, v) in values)
            {
                Values[k] = v;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Secrets : ISecrets
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public SecretStatus Status(string key) => Values.ContainsKey(key) ? new SecretStatus(true, DateTimeOffset.UnixEpoch, "setup") : SecretStatus.Missing;

        public string? TryGet(string key) => Values.GetValueOrDefault(key);

        public void Store(string key, string value, string by) => Values[key] = value;

        public bool Remove(string key) => Values.Remove(key);
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

    private sealed class Probe : IArtifactStorageProbe
    {
        public ArtifactS3Options? Tried { get; private set; }

        public int Calls { get; private set; }

        public Task<StepCheck> TestAsync(ArtifactS3Options options, CancellationToken cancellationToken)
        {
            Tried = options;
            Calls++;
            return Task.FromResult(new StepCheck(true, "ok"));
        }
    }
}
