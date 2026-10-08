using Agentd.Application.Jobs;
using Agentd.Application.Setup;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Tests.Setup;

[TestClass]
public sealed class SetupServiceTests
{
    private const string ConnectionString = "Host=db;Username=agentd;Password=s3cret-pw;Database=agentd";
    private const string Pat = "pat-SECRET-123";

    private readonly FakeConfig _config = new();
    private readonly FakeSecrets _secrets = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeDatabase _database = new();
    private readonly FakeAzureDevOps _azureDevOps = new();
    private readonly FakeGitKey _gitKey = new();
    private readonly FakeClaude _claude = new();
    private readonly FakeChat _chat = new();
    private readonly FakeRemote _remote = new();
    private readonly FakeLink _link = new();

    [TestMethod]
    public async Task Saving_the_database_stores_a_secret_and_audits_it_without_the_value()
    {
        var result = await Service().SaveDatabaseAsync(ConnectionString, "setup", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Value!.RestartRequired);
        Assert.AreEqual(ConnectionString, _secrets.Values[SetupService.ConnectionStringSecret]);
        Assert.IsEmpty(_config.Values, "never in agentd.json");
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

    [TestMethod]
    public async Task Saving_azure_devops_writes_the_settings_and_keeps_the_pat_a_secret()
    {
        var result = await Service().SaveAzureDevOpsAsync(new AzureDevOpsInput("https://dev.azure.com/myorg/", " Portal ", "pat", Pat), "setup", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("myorg", _config.Values["AzureDevOps:Organization"]);
        Assert.AreEqual("Portal", _config.Values["AzureDevOps:Project"]);
        Assert.AreEqual("Pat", _config.Values["AzureDevOps:Auth"]);
        Assert.AreEqual(Pat, _secrets.Values[SetupService.PatSecret]);
        Assert.IsFalse(_config.Values.ContainsValue(Pat));
        CollectionAssert.AreEquivalent(new[] { "AzureDevOps:Organization", "AzureDevOps:Project", "AzureDevOps:Auth", SetupService.PatSecret }, _audit.Changes.Select(c => c.Key).ToList());
        var step = Service().GetAzureDevOps();
        Assert.AreEqual(("myorg", "Portal", "Pat", true), (step.Organization, step.Project, step.Auth, step.Pat.Set));
    }

    [TestMethod]
    public async Task A_pat_is_required_once_and_then_kept_when_left_empty()
    {
        var missing = await Service().SaveAzureDevOpsAsync(new AzureDevOpsInput("myorg", "Portal", "Pat", null), "setup", CancellationToken.None);
        _secrets.Values[SetupService.PatSecret] = Pat;
        var kept = await Service().SaveAzureDevOpsAsync(new AzureDevOpsInput("myorg", "Portal", "Pat", ""), "setup", CancellationToken.None);

        Assert.AreEqual("validation", missing.Error?.Code);
        Assert.IsTrue(kept.IsSuccess);
        Assert.AreEqual(Pat, _secrets.Values[SetupService.PatSecret]);
        Assert.IsFalse(_audit.Changes.Any(c => c.Key == SetupService.PatSecret), "the PAT didn't change");
    }

    [TestMethod]
    public async Task Az_login_needs_no_pat()
    {
        var result = await Service().SaveAzureDevOpsAsync(new AzureDevOpsInput("myorg", "Portal", "AzCli", null), "setup", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("AzCli", _config.Values["AzureDevOps:Auth"]);
    }

    [TestMethod]
    [DataRow("", "Portal", "Pat")]
    [DataRow("https://github.com/myorg", "Portal", "Pat")]
    [DataRow("myorg", " ", "Pat")]
    [DataRow("myorg", "Portal", "Kerberos")]
    public async Task Invalid_azure_devops_settings_are_refused(string organization, string project, string auth)
    {
        var result = await Service().SaveAzureDevOpsAsync(new AzureDevOpsInput(organization, project, auth, Pat), "setup", CancellationToken.None);

        Assert.AreEqual("validation", result.Error?.Code);
        Assert.IsEmpty(_config.Values);
        Assert.IsEmpty(_secrets.Values);
    }

    [TestMethod]
    public async Task Test_azure_devops_uses_the_saved_pat_when_none_is_given()
    {
        _secrets.Values[SetupService.PatSecret] = Pat;

        var check = await Service().TestAzureDevOpsAsync(new AzureDevOpsInput("myorg", "Portal", "Pat", null), CancellationToken.None);

        Assert.IsTrue(check.Ok);
        Assert.AreEqual(new AzureDevOpsConnection("myorg", "Portal", true, Pat), _azureDevOps.Tested.Single());
    }

    [TestMethod]
    public async Task Test_azure_devops_without_input_tests_the_saved_settings()
    {
        _config.Values["AzureDevOps:Organization"] = "myorg";
        _config.Values["AzureDevOps:Project"] = "Portal";

        await Service().TestAzureDevOpsAsync(null, CancellationToken.None);

        Assert.AreEqual(new AzureDevOpsConnection("myorg", "Portal", false, null), _azureDevOps.Tested.Single());
    }

    [TestMethod]
    [DataRow("myorg", "myorg")]
    [DataRow("https://dev.azure.com/myorg", "myorg")]
    [DataRow("https://dev.azure.com/my%20org/Portal/_git/x", "my org")]
    [DataRow("https://myorg.visualstudio.com/", "myorg")]
    [DataRow("https://example.com/myorg", null)]
    [DataRow("my/org", null)]
    [DataRow("  ", null)]
    public void Organization_names_come_from_names_or_urls(string value, string? expected) =>
        Assert.AreEqual(expected, SetupService.OrganizationName(value));

    [TestMethod]
    public async Task Generating_the_git_key_is_audited_and_never_replaces_one()
    {
        var first = await Service().GenerateGitKeyAsync("setup", CancellationToken.None);
        var second = await Service().GenerateGitKeyAsync("setup", CancellationToken.None);

        Assert.IsTrue(first.IsSuccess);
        Assert.AreEqual("ssh-ed25519 AAAA agentd@test", first.Value!.PublicKey);
        Assert.AreEqual("conflict", second.Error?.Code);
        Assert.AreEqual(1, _gitKey.Generated);
        Assert.AreEqual(("Git:SshKey", "generated"), (_audit.Changes.Single().Key, _audit.Changes.Single().Action));
        Assert.IsTrue(Service().GetGitKey().Exists);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("  ")]
    [DataRow("--upload-pack=evil")]
    [DataRow("git@host:repo with space")]
    public async Task Testing_git_access_needs_a_plain_url(string? url)
    {
        var check = await Service().TestGitAccessAsync(url, CancellationToken.None);

        Assert.IsFalse(check.Ok);
        Assert.IsEmpty(_gitKey.Tested);
    }

    [TestMethod]
    public async Task Testing_git_access_passes_the_url_on()
    {
        var check = await Service().TestGitAccessAsync(" git@ssh.dev.azure.com:v3/myorg/Portal/sysmin ", CancellationToken.None);

        Assert.IsTrue(check.Ok);
        CollectionAssert.AreEqual(new[] { "git@ssh.dev.azure.com:v3/myorg/Portal/sysmin" }, _gitKey.Tested);
    }

    [TestMethod]
    public async Task The_claude_token_is_a_secret_and_tests_fall_back_to_the_saved_one_then_the_servers_login()
    {
        await Service().TestClaudeAsync(null, CancellationToken.None);
        var saved = await Service().SaveClaudeTokenAsync(" sk-ant-oat01-0123456789abcdef ", "setup", CancellationToken.None);
        await Service().TestClaudeAsync("", CancellationToken.None);
        await Service().TestClaudeAsync("sk-ant-oat01-other-000000000000", CancellationToken.None);

        Assert.IsTrue(saved.Value!.RestartRequired);
        Assert.AreEqual("sk-ant-oat01-0123456789abcdef", _secrets.Values[SetupService.ClaudeTokenSecret]);
        CollectionAssert.AreEqual(new[] { null, "sk-ant-oat01-0123456789abcdef", "sk-ant-oat01-other-000000000000" }, _claude.Tested);
        Assert.IsTrue((await Service().GetClaudeAsync(CancellationToken.None)).Token.Set);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("short")]
    [DataRow("sk-ant-oat01 with spaces in the middle")]
    public async Task A_claude_token_must_be_one_whole_line(string token)
    {
        var result = await Service().SaveClaudeTokenAsync(token, "setup", CancellationToken.None);

        Assert.AreEqual("validation", result.Error?.Code);
        Assert.IsEmpty(_secrets.Values);
    }

    [TestMethod]
    public async Task Removing_the_claude_token_is_audited_once()
    {
        _secrets.Values[SetupService.ClaudeTokenSecret] = "sk-ant-oat01-0123456789abcdef";

        await Service().RemoveClaudeTokenAsync("setup", CancellationToken.None);
        await Service().RemoveClaudeTokenAsync("setup", CancellationToken.None);

        Assert.IsEmpty(_secrets.Values);
        Assert.AreEqual("removed", _audit.Changes.Single().Action);
    }

    private const string Guild = "770517485715193877";
    private const string Channel = "1555955347544608809";
    private const string Me = "710392908099878953";

    [TestMethod]
    public async Task Saving_chat_enables_discord_stores_the_token_and_adds_you_as_an_admin()
    {
        _config.Values["Users:0:Name"] = "alice";

        var result = await Service().SaveChatAsync(new ChatInput(true, Guild, Channel, "bot-SECRET", "tngo", Me), "setup", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual("true", _config.Values["Messaging:Providers:Discord:Enabled"]);
        Assert.AreEqual(Channel, _config.Values["Messaging:Providers:Discord:ChannelId"]);
        Assert.AreEqual("bot-SECRET", _secrets.Values[SetupService.DiscordTokenSecret]);
        Assert.IsFalse(_config.Values.ContainsValue("bot-SECRET"));
        Assert.AreEqual(("tngo", "Admin", Me), (_config.Values["Users:1:Name"], _config.Values["Users:1:Roles:0"], _config.Values["Users:1:Identities:Discord"]));
        CollectionAssert.AreEqual(new[] { new ChatUser("tngo", Me) }, Service().GetChat().Users.ToList());
    }

    [TestMethod]
    public async Task An_existing_user_gets_the_discord_id()
    {
        _config.Values["Users:0:Name"] = "TNGO";
        _secrets.Values[SetupService.DiscordTokenSecret] = "saved";

        await Service().SaveChatAsync(new ChatInput(true, Guild, Channel, null, "tngo", Me), "setup", CancellationToken.None);

        Assert.AreEqual(Me, _config.Values["Users:0:Identities:Discord"]);
        Assert.IsFalse(_config.Values.ContainsKey("Users:1:Name"));
        Assert.AreEqual("saved", _secrets.Values[SetupService.DiscordTokenSecret], "kept when left empty");
    }

    [TestMethod]
    [DataRow("123", Channel, "bot", null, null)]
    [DataRow(Guild, "general", "bot", null, null)]
    [DataRow(Guild, Channel, null, null, null)]
    [DataRow(Guild, Channel, "bot", null, Me)]
    [DataRow(Guild, Channel, "bot", "tngo", "@tngo")]
    public async Task Invalid_chat_settings_are_refused(string guild, string channel, string? token, string? name, string? id)
    {
        var result = await Service().SaveChatAsync(new ChatInput(true, guild, channel, token, name, id), "setup", CancellationToken.None);

        Assert.AreEqual("validation", result.Error?.Code);
        Assert.IsEmpty(_config.Values);
    }

    [TestMethod]
    public async Task Turning_chat_off_only_disables_it()
    {
        var result = await Service().SaveChatAsync(new ChatInput(false, null, null, null, null, null), "setup", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("false", _config.Values.Single().Value);
    }

    [TestMethod]
    public async Task Testing_chat_uses_the_saved_token_when_none_is_given()
    {
        _secrets.Values[SetupService.DiscordTokenSecret] = "saved";

        await Service().TestChatAsync(new ChatInput(true, Guild, Channel, " ", null, null), CancellationToken.None);

        Assert.AreEqual(new ChatConnection("saved", Guild, Channel), _chat.Tested.Single());
    }

    private const string RepoUrl = "git@ssh.dev.azure.com:v3/ermsystem/Portal/sysmin";

    [TestMethod]
    public async Task Adding_a_repository_detects_its_branch_defaults_the_tag_and_appends_it()
    {
        _config.Values["Repositories:Items:0:Url"] = "git@ssh.dev.azure.com:v3/ermsystem/Portal/other";

        var result = await Service().AddRepositoryAsync(new RepositoryInput($" {RepoUrl} ", null, null, null, null), "setup", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual(new RepositoryEntry(RepoUrl, "sysmin", "develop", "repo:sysmin", []), result.Value with { MatchAreaPaths = [] });
        Assert.AreEqual(RepoUrl, _config.Values["Repositories:Items:1:Url"]);
        Assert.AreEqual("develop", _config.Values["Repositories:Items:1:BaseBranch"]);
        Assert.AreEqual("repo:sysmin", _config.Values["Repositories:Items:1:MatchTag"]);
        Assert.AreEqual(2, Service().GetRepositories().Count);
        Assert.AreEqual(("Repositories:Items:1", "added"), (_audit.Changes.Single().Key, _audit.Changes.Single().Action));
    }

    [TestMethod]
    public async Task Area_paths_replace_the_default_tag()
    {
        var result = await Service().AddRepositoryAsync(new RepositoryInput(RepoUrl, "sys", "main", null, ["Portal\\Sysmin", " ", "portal\\sysmin"]), "setup", CancellationToken.None);

        Assert.IsNull(result.Value!.MatchTag);
        CollectionAssert.AreEqual(new[] { "Portal\\Sysmin" }, result.Value.MatchAreaPaths.ToList());
        Assert.AreEqual("main", _config.Values["Repositories:Items:0:BaseBranch"]);
        Assert.IsEmpty(_remote.Asked, "a given base branch needs no lookup");
        CollectionAssert.AreEqual(new[] { "Portal\\Sysmin" }, Service().GetRepositories().Single().MatchAreaPaths.ToList());
    }

    [TestMethod]
    public async Task A_repository_is_refused_when_unparseable_unreachable_or_already_added()
    {
        var bad = await Service().AddRepositoryAsync(new RepositoryInput("https://github.com/x/y", null, null, null, null), "setup", CancellationToken.None);
        _remote.Fail = true;
        var unreachable = await Service().AddRepositoryAsync(new RepositoryInput(RepoUrl, null, null, null, null), "setup", CancellationToken.None);
        _remote.Fail = false;
        _config.Values["Repositories:Items:0:Url"] = RepoUrl;
        var twice = await Service().AddRepositoryAsync(new RepositoryInput(RepoUrl, null, null, null, null), "setup", CancellationToken.None);

        Assert.AreEqual("validation", bad.Error?.Code);
        Assert.AreEqual("validation", unreachable.Error?.Code);
        Assert.Contains("Permission denied", unreachable.Error!.Message);
        Assert.AreEqual("conflict", twice.Error?.Code);
        Assert.IsEmpty(_audit.Changes);
    }

    [TestMethod]
    public async Task Testing_a_repository_reports_its_default_branch()
    {
        var ok = await Service().TestRepositoryAsync(RepoUrl, CancellationToken.None);
        _remote.Fail = true;
        var failed = await Service().TestRepositoryAsync(RepoUrl, CancellationToken.None);

        Assert.AreEqual("Reached ermsystem/Portal/sysmin; its default branch is develop.", ok.Message);
        Assert.IsFalse(failed.Ok);
        Assert.Contains("Git access", failed.Fix!);
    }

    [TestMethod]
    public async Task Finishing_needs_the_database_azure_devops_and_claude()
    {
        var early = await Service().FinishAsync("setup", CancellationToken.None);

        Assert.AreEqual("validation", early.Error?.Code);
        Assert.Contains("Database, Azure DevOps", early.Error!.Message);
        Assert.IsFalse(_link.Revoked);
        Assert.IsFalse(_config.Values.ContainsKey("Setup:CompletedAt"));
    }

    [TestMethod]
    public async Task Finishing_records_completion_and_revokes_the_link()
    {
        Ready();

        var review = await Service().ReviewAsync(CancellationToken.None);
        var result = await Service().FinishAsync("setup", CancellationToken.None);

        Assert.IsTrue(review.Where(i => i.Required).All(i => i.Check.Ok));
        CollectionAssert.AreEqual(new[] { "database", "azure-devops", "git", "claude", "chat", "repositories" }, review.Select(i => i.Step).ToList());
        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual(result.Value!.CompletedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture), _config.Values["Setup:CompletedAt"]);
        Assert.IsTrue(_link.Revoked);
        Assert.AreEqual("Setup:CompletedAt", _audit.Changes.Last().Key);
    }

    [TestMethod]
    public async Task Optional_steps_warn_but_dont_block()
    {
        Ready();
        _config.Values["Messaging:Providers:Discord:Enabled"] = "true";   // on, but no token or user

        var review = await Service().ReviewAsync(CancellationToken.None);
        var result = await Service().FinishAsync("setup", CancellationToken.None);

        Assert.IsFalse(review.Single(i => i.Step == "chat").Check.Ok);
        Assert.IsFalse(review.Single(i => i.Step == "repositories").Check.Ok);
        Assert.IsTrue(result.IsSuccess);
    }

    private void Ready()
    {
        _secrets.Values[SetupService.ConnectionStringSecret] = ConnectionString;
        _config.Values["AzureDevOps:Organization"] = "myorg";
        _config.Values["AzureDevOps:Project"] = "Portal";
    }

    private static readonly ProfileInput s_deepseek = new("DeepSeek", "https://api.deepseek.com/anthropic/", "deepseek-flash[1m]", "deepseek-flash", "sk-SECRET-deepseek");

    [TestMethod]
    public async Task A_provider_is_saved_with_its_key_as_a_secret()
    {
        var result = await Service().SaveProfileAsync(s_deepseek, "setup", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual("https://api.deepseek.com/anthropic", _config.Values["Models:Profiles:deepseek:BaseUrl"], "a lowercase name, no trailing slash");
        Assert.AreEqual(("deepseek-flash[1m]", "deepseek-flash", "AnthropicCompatible"),
            (_config.Values["Models:Profiles:deepseek:Model"], _config.Values["Models:Profiles:deepseek:SmallModel"], _config.Values["Models:Profiles:deepseek:Kind"]));
        Assert.AreEqual("sk-SECRET-deepseek", _secrets.Values["Models:Profiles:deepseek:ApiKey"]);
        Assert.IsFalse(_config.Values.ContainsValue("sk-SECRET-deepseek"));
        var listed = Service().GetModels().Profiles.Single();
        Assert.AreEqual(("deepseek", true), (listed.Name, listed.ApiKey.Set));
    }

    [TestMethod]
    [DataRow("Deep Seek", "https://api.deepseek.com/anthropic", "sk", "name")]
    [DataRow("deepseek", "http://api.deepseek.com/anthropic", "sk", "https")]
    [DataRow("deepseek", "https://api.deepseek.com/anthropic", "", "API key")]
    public async Task A_provider_that_cant_work_is_refused(string name, string url, string key, string expected)
    {
        var result = await Service().SaveProfileAsync(new ProfileInput(name, url, "m", null, key), "setup", CancellationToken.None);

        Assert.AreEqual("validation", result.Error?.Code);
        StringAssert.Contains(result.Error!.Message, expected);
        Assert.IsEmpty(_config.Values);
    }

    [TestMethod]
    public async Task Steps_get_a_model_effort_and_provider_and_empty_values_clear_them()
    {
        await Service().SaveProfileAsync(s_deepseek, "setup", CancellationToken.None);

        var result = await Service().SaveStepsAsync(
        [
            new StepView("plan", "claude-opus-5-5", "High", null),
            new StepView("implement", null, null, "deepseek"),
            new StepView("review", "claude-opus-5-5", "high", " "),
        ], "setup", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual("high", _config.Values["Jobs:Steps:plan:Effort"]);
        Assert.AreEqual("deepseek", _config.Values["Jobs:Steps:implement:Profile"]);
        Assert.IsNull(_config.Values["Jobs:Steps:implement:Model"], "cleared: the profile's model applies");
        var steps = Service().GetModels().Steps;
        CollectionAssert.AreEqual(SetupService.ModelSteps.ToList(), steps.Select(s => s.Step).ToList(), "every step is listed");
        Assert.AreEqual("deepseek", steps.Single(s => s.Step == "implement").Profile);
    }

    [TestMethod]
    [DataRow("review", null, "deepseek", "runs on Claude")]
    [DataRow("implement", null, "glm", "no provider `glm`")]
    [DataRow("implement", "ultra", null, "effort level")]
    [DataRow("deploy", null, null, "isn't a step")]
    public async Task A_step_that_cant_work_is_refused(string step, string? effort, string? profile, string expected)
    {
        await Service().SaveProfileAsync(s_deepseek, "setup", CancellationToken.None);

        var result = await Service().SaveStepsAsync([new StepView(step, null, effort, profile)], "setup", CancellationToken.None);

        StringAssert.Contains(result.Error!.Message, expected);
    }

    [TestMethod]
    public async Task A_provider_in_use_isnt_removed_and_one_not_in_use_takes_its_key_along()
    {
        await Service().SaveProfileAsync(s_deepseek, "setup", CancellationToken.None);
        await Service().SaveStepsAsync([new StepView("implement", null, null, "deepseek")], "setup", CancellationToken.None);

        var refused = await Service().RemoveProfileAsync("deepseek", "setup", CancellationToken.None);
        await Service().SaveStepsAsync([new StepView("implement", null, null, null)], "setup", CancellationToken.None);
        var removed = await Service().RemoveProfileAsync("deepseek", "setup", CancellationToken.None);

        Assert.AreEqual("conflict", refused.Error?.Code);
        StringAssert.Contains(refused.Error!.Message, "implement");
        Assert.IsTrue(removed.IsSuccess);
        Assert.IsFalse(_secrets.Values.ContainsKey("Models:Profiles:deepseek:ApiKey"));
    }

    [TestMethod]
    public async Task Testing_a_provider_uses_its_saved_key_and_never_the_subscription()
    {
        await Service().SaveProfileAsync(s_deepseek, "setup", CancellationToken.None);

        var check = await Service().TestProfileAsync(s_deepseek with { ApiKey = null }, CancellationToken.None);

        Assert.IsTrue(check.Ok);
        var (name, profile) = _claude.Profiles.Single();
        Assert.AreEqual(("deepseek", "sk-SECRET-deepseek", "deepseek-flash[1m]"), (name, profile.ApiKey, profile.Model));
        Assert.IsEmpty(_claude.Tested, "not the Claude login test");
    }

    private SetupService Service() => new(_config, _secrets, _audit, _database, _azureDevOps, _gitKey, _claude, _chat, _remote, _link, Options.Create(new JobOptions()), TimeProvider.System);

    private sealed class FakeConfig : IConfigWriter
    {
        public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Read(string key) => Values.GetValueOrDefault(key);

        public IReadOnlyList<string> Children(string section) =>
            [.. Values.Keys.Where(k => k.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase)).Select(k => k[(section.Length + 1)..].Split(':')[0]).Distinct(StringComparer.OrdinalIgnoreCase)];

        public Task SetAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken)
        {
            foreach (var (k, v) in values)
            {
                Values[k] = v;
            }

            return Task.CompletedTask;
        }
    }

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

    private sealed class FakeAzureDevOps : IAzureDevOpsProbe
    {
        public List<AzureDevOpsConnection> Tested { get; } = [];

        public Task<StepCheck> TestAsync(AzureDevOpsConnection connection, JobOptions jobs, CancellationToken cancellationToken)
        {
            Tested.Add(connection);
            return Task.FromResult(new StepCheck(true, "signed in"));
        }
    }

    private sealed class FakeGitKey : IGitKey
    {
        private GitKeyInfo? _key;

        public int Generated { get; private set; }

        public List<string> Tested { get; } = [];

        public GitKeyInfo? Read() => _key;

        public Task<GitKeyInfo> GenerateAsync(string comment, CancellationToken cancellationToken)
        {
            Generated++;
            _key = new GitKeyInfo("/home/agentd/.agentd/ssh/id_ed25519", "ssh-ed25519 AAAA agentd@test", "SHA256:abc");
            return Task.FromResult(_key);
        }

        public Task<StepCheck> TestAsync(string url, CancellationToken cancellationToken)
        {
            Tested.Add(url);
            return Task.FromResult(new StepCheck(true, "reached"));
        }
    }

    private sealed class FakeClaude : IClaudeProbe
    {
        public List<string?> Tested { get; } = [];

        public Task<ClaudeLogin> StatusAsync(CancellationToken cancellationToken) => Task.FromResult(new ClaudeLogin(true, "2.1.300", false, null, null));

        public Task<StepCheck> TestAsync(string? token, CancellationToken cancellationToken)
        {
            Tested.Add(token);
            return Task.FromResult(new StepCheck(true, "answered"));
        }

        public List<(string Name, ModelProfile Profile)> Profiles { get; } = [];

        public Task<StepCheck> TestProfileAsync(string name, ModelProfile profile, CancellationToken cancellationToken)
        {
            Profiles.Add((name, profile));
            return Task.FromResult(new StepCheck(true, "answered through the profile"));
        }
    }

    private sealed class FakeChat : IChatProbe
    {
        public List<ChatConnection> Tested { get; } = [];

        public Task<StepCheck> TestAsync(ChatConnection connection, CancellationToken cancellationToken)
        {
            Tested.Add(connection);
            return Task.FromResult(new StepCheck(true, "posted"));
        }
    }

    private sealed class FakeRemote : Ports.IGitRemote
    {
        public bool Fail { get; set; }

        public List<string> Asked { get; } = [];

        public Task<string> GetDefaultBranchAsync(string remoteUrl, CancellationToken cancellationToken)
        {
            Asked.Add(remoteUrl);
            return Fail ? throw new InvalidOperationException("Permission denied (publickey).") : Task.FromResult("develop");
        }
    }

    private sealed class FakeLink : ISetupLink
    {
        public bool Revoked { get; private set; }

        public void Revoke() => Revoked = true;
    }
}
