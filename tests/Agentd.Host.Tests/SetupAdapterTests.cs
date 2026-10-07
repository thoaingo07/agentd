using System.Text.Json.Nodes;
using Agentd.Application.Setup;
using Agentd.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Host.Tests;

[TestClass]
public sealed class SetupAdapterTests : IDisposable
{
    private readonly ConfigHome _home = new ConfigHome(Directory.CreateTempSubdirectory("agentd-setup-").FullName).EnsureCreated();

    [TestMethod]
    public async Task Writes_merge_into_agentd_json_case_insensitively_and_keep_other_keys()
    {
        File.WriteAllText(_home.ConfigFile, """
            {
              // comments are allowed
              "azureDevOps": { "organization": "old", "Extra": 1 },
              "Scheduler": { "MaxConcurrent": 3 },
            }
            """);
        using var file = new AgentdJsonFile(_home);

        await file.SetAsync(new Dictionary<string, string?> { ["AzureDevOps:Organization"] = "myorg", ["AzureDevOps:Project"] = "Portal", ["Scheduler:MaxConcurrent"] = null }, CancellationToken.None);

        var json = JsonNode.Parse(File.ReadAllText(_home.ConfigFile))!;
        Assert.AreEqual("myorg", json["azureDevOps"]!["organization"]!.GetValue<string>(), "the existing spelling is kept");
        Assert.AreEqual("Portal", json["azureDevOps"]!["Project"]!.GetValue<string>());
        Assert.AreEqual(1, json["azureDevOps"]!["Extra"]!.GetValue<int>());
        Assert.IsNull(json["Scheduler"]!["MaxConcurrent"]);
        Assert.AreEqual("myorg", file.Read("AzureDevOps:Organization"));
        Assert.AreEqual("1", file.Read("AZUREDEVOPS:EXTRA"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_home.ConfigFile));
        }

        var settled = new ConfigurationBuilder().AddJsonFile(_home.ConfigFile).Build();
        Assert.AreEqual("Portal", settled["AzureDevOps:Project"], "still a valid configuration file");
    }

    [TestMethod]
    public async Task A_new_file_is_created_and_missing_keys_fall_back_to_the_running_configuration()
    {
        var running = new ConfigurationBuilder().AddInMemoryCollection([new("Agentd:AzureDevOps:Project", "FromEnv")]).Build();
        using var file = new AgentdJsonFile(_home, running);

        Assert.AreEqual("FromEnv", file.Read("AzureDevOps:Project"));
        await file.SetAsync(new Dictionary<string, string?> { ["Setup:CompletedAt"] = "2026-10-06T12:00:00Z" }, CancellationToken.None);

        Assert.IsTrue(SetupState.IsCompleteIn(_home));
    }

    [TestMethod]
    public async Task Numeric_segments_are_array_items_like_configuration()
    {
        File.WriteAllText(_home.ConfigFile, """{ "Users": [ { "Name": "alice", "Roles": [ "Admin" ] } ] }""");
        using var file = new AgentdJsonFile(_home);

        await file.SetAsync(new Dictionary<string, string?>
        {
            ["Users:0:Identities:Discord"] = "111111111111111111",
            ["Users:1:Name"] = "bob",
            ["Users:1:Roles:0"] = "Admin",
        }, CancellationToken.None);

        var json = JsonNode.Parse(File.ReadAllText(_home.ConfigFile))!;
        Assert.AreEqual(2, json["Users"]!.AsArray().Count);
        Assert.AreEqual("alice", file.Read("Users:0:Name"));
        Assert.AreEqual("111111111111111111", file.Read("Users:0:Identities:Discord"));
        Assert.AreEqual("Admin", json["Users"]![1]!["Roles"]![0]!.GetValue<string>());
        var settled = new ConfigurationBuilder().AddJsonFile(_home.ConfigFile).Build();
        Assert.AreEqual("bob", settled["Users:1:Name"]);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => file.SetAsync(new Dictionary<string, string?> { ["Users:5:Name"] = "gap" }, CancellationToken.None));
    }

    [TestMethod]
    public async Task A_new_array_is_created_when_the_next_segment_is_an_index()
    {
        using var file = new AgentdJsonFile(_home);

        await file.SetAsync(new Dictionary<string, string?> { ["Users:0:Name"] = "alice" }, CancellationToken.None);

        Assert.IsInstanceOfType<JsonArray>(JsonNode.Parse(File.ReadAllText(_home.ConfigFile))!["Users"]);
    }

    [TestMethod]
    public void Secret_status_has_who_and_when_but_never_the_value()
    {
        var secrets = new StoredSecrets(new SecretStore(_home));

        secrets.Store("AzureDevOps:Pat", "pat-SECRET", "setup");

        var status = secrets.Status("AzureDevOps:Pat");
        Assert.IsTrue(status.Set);
        Assert.AreEqual("setup", status.UpdatedBy);
        Assert.AreEqual("pat-SECRET", secrets.TryGet("AzureDevOps:Pat"), "server-side only (to test a connection)");
        Assert.AreEqual(SecretStatus.Missing, secrets.Status("Discord:BotToken"));
    }

    [TestMethod]
    public async Task The_audit_appends_one_line_per_change_owner_only()
    {
        using var audit = new FileSettingsAudit(_home, NullLogger<FileSettingsAudit>.Instance);

        await audit.RecordAsync([new SettingsChange("AzureDevOps:Pat", "secret", "set", "setup", DateTimeOffset.UnixEpoch)], CancellationToken.None);
        await audit.RecordAsync([new SettingsChange("AzureDevOps:Project", "config", "set", "local", DateTimeOffset.UnixEpoch)], CancellationToken.None);

        var lines = File.ReadAllLines(audit.FilePath);
        Assert.HasCount(2, lines);
        var first = JsonNode.Parse(lines[0])!;
        Assert.AreEqual("settings.changed", first["event"]!.GetValue<string>());
        Assert.AreEqual("AzureDevOps:Pat", first["key"]!.GetValue<string>());
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(audit.FilePath));
        }
    }

    public void Dispose() => Directory.Delete(_home.Root, recursive: true);
}
