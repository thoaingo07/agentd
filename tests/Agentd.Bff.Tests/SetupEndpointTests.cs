using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentd.Application.Jobs;
using Agentd.Application.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class SetupEndpointTests : IDisposable
{
    private const string ConnectionString = "Host=db;Username=agentd;Password=pw-SECRET;Database=agentd";
    private const string Pat = "pat-SECRET-123";

    private readonly SetupSessionTests _session = new();
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _config = new(StringComparer.OrdinalIgnoreCase);

    [TestMethod]
    public async Task Secrets_go_in_but_never_come_back_out()
    {
        await using var app = await StartAsync();
        var client = await SetupClient.SignInAsync(app);

        using var saveDb = await client.SendAsync(HttpMethod.Put, "/api/setup/database", new { connectionString = ConnectionString });
        using var saveAdo = await client.SendAsync(HttpMethod.Put, "/api/setup/azure-devops", new { organization = "myorg", project = "Portal", auth = "Pat", pat = Pat });
        using var db = await client.SendAsync(HttpMethod.Get, "/api/setup/database");
        using var ado = await client.SendAsync(HttpMethod.Get, "/api/setup/azure-devops");
        using var test = await client.SendAsync(HttpMethod.Post, "/api/setup/azure-devops/test", new { organization = "myorg", project = "Portal", auth = "Pat" });

        foreach (var response in new[] { saveDb, saveAdo, db, ado, test })
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SECRET", body);
        }

        Assert.AreEqual(ConnectionString, _secrets[SetupService.ConnectionStringSecret]);
        Assert.AreEqual(Pat, _secrets[SetupService.PatSecret]);
        var step = JsonDocument.Parse(await ado.Content.ReadAsStringAsync()).RootElement;
        Assert.IsTrue(step.GetProperty("pat").GetProperty("set").GetBoolean());
        Assert.AreEqual("myorg", step.GetProperty("organization").GetString());
        Assert.IsTrue(JsonDocument.Parse(await saveDb.Content.ReadAsStringAsync()).RootElement.GetProperty("restartRequired").GetBoolean());
    }

    [TestMethod]
    public async Task Every_step_needs_the_setup_session()
    {
        await using var app = await StartAsync();
        var client = SetupSessionTests.Client(app);

        using var get = await client.GetAsync(new Uri("/api/setup/database", UriKind.Relative));
        using var put = await client.PutAsJsonAsync(new Uri("/api/setup/database", UriKind.Relative), new { connectionString = ConnectionString });

        Assert.AreEqual(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, put.StatusCode);
        Assert.IsEmpty(_secrets);
    }

    [TestMethod]
    public async Task Changes_need_the_antiforgery_token()
    {
        await using var app = await StartAsync();
        var client = await SetupClient.SignInAsync(app);

        using var response = await client.SendAsync(HttpMethod.Put, "/api/setup/database", new { connectionString = ConnectionString }, withToken: false);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsEmpty(_secrets);
    }

    [TestMethod]
    public async Task Invalid_input_is_a_400_with_the_reason()
    {
        await using var app = await StartAsync();
        var client = await SetupClient.SignInAsync(app);

        using var badOrg = await client.SendAsync(HttpMethod.Put, "/api/setup/azure-devops", new { organization = "https://github.com/x", project = "Portal", auth = "AzCli" });
        using var tooLong = await client.SendAsync(HttpMethod.Put, "/api/setup/database", new { connectionString = new string('x', 5000) });

        Assert.AreEqual(HttpStatusCode.BadRequest, badOrg.StatusCode);
        Assert.Contains("organization", await badOrg.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [TestMethod]
    public async Task The_git_key_is_generated_once_and_only_its_public_half_is_shown()
    {
        await using var app = await StartAsync();
        var client = await SetupClient.SignInAsync(app);

        using var before = await client.SendAsync(HttpMethod.Get, "/api/setup/git-key");
        using var generate = await client.SendAsync(HttpMethod.Post, "/api/setup/git-key");
        using var again = await client.SendAsync(HttpMethod.Post, "/api/setup/git-key");
        using var test = await client.SendAsync(HttpMethod.Post, "/api/setup/git-key/test", new { url = "git@ssh.dev.azure.com:v3/o/p/r" });

        Assert.IsFalse(JsonDocument.Parse(await before.Content.ReadAsStringAsync()).RootElement.GetProperty("exists").GetBoolean());
        var key = JsonDocument.Parse(await generate.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual("ssh-ed25519 AAAA agentd@test", key.GetProperty("publicKey").GetString());
        Assert.DoesNotContain("PRIVATE", await generate.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, test.StatusCode);
    }

    [TestMethod]
    public async Task The_claude_token_goes_in_and_only_its_status_comes_out()
    {
        await using var app = await StartAsync();
        var client = await SetupClient.SignInAsync(app);

        using var save = await client.SendAsync(HttpMethod.Put, "/api/setup/claude", new { token = "sk-ant-oat01-SECRET-0123456789" });
        using var get = await client.SendAsync(HttpMethod.Get, "/api/setup/claude");
        using var test = await client.SendAsync(HttpMethod.Post, "/api/setup/claude/test", new { token = (string?)null });
        using var remove = await client.SendAsync(HttpMethod.Delete, "/api/setup/claude/token");

        foreach (var response in new[] { save, get, test, remove })
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain("SECRET", await response.Content.ReadAsStringAsync());
        }

        var step = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement;
        Assert.IsTrue(step.GetProperty("token").GetProperty("set").GetBoolean());
        Assert.AreEqual("2.1.300", step.GetProperty("server").GetProperty("version").GetString());
        Assert.IsFalse(_secrets.ContainsKey(SetupService.ClaudeTokenSecret), "removed");
    }

    [TestMethod]
    public async Task The_bot_token_goes_in_and_only_its_status_comes_out()
    {
        await using var app = await StartAsync();
        var client = await SetupClient.SignInAsync(app);
        var body = new { enabled = true, guildId = "770517485715193877", channelId = "1555955347544608809", botToken = "bot-SECRET", userName = "tngo", userDiscordId = "710392908099878953" };

        using var save = await client.SendAsync(HttpMethod.Put, "/api/setup/chat", body);
        using var get = await client.SendAsync(HttpMethod.Get, "/api/setup/chat");
        using var test = await client.SendAsync(HttpMethod.Post, "/api/setup/chat/test", body with { });

        foreach (var response in new[] { save, get, test })
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain("SECRET", await response.Content.ReadAsStringAsync());
        }

        var step = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement;
        Assert.IsTrue(step.GetProperty("botToken").GetProperty("set").GetBoolean());
        Assert.AreEqual("710392908099878953", step.GetProperty("users")[0].GetProperty("discordId").GetString());
    }

    [TestMethod]
    public async Task Repositories_are_added_once_and_listed()
    {
        await using var app = await StartAsync();
        var client = await SetupClient.SignInAsync(app);
        var body = new { url = "git@ssh.dev.azure.com:v3/ermsystem/Portal/sysmin" };

        using var add = await client.SendAsync(HttpMethod.Post, "/api/setup/repositories", body);
        using var again = await client.SendAsync(HttpMethod.Post, "/api/setup/repositories", body);
        using var list = await client.SendAsync(HttpMethod.Get, "/api/setup/repositories");
        using var test = await client.SendAsync(HttpMethod.Post, "/api/setup/repositories/test", body);

        Assert.AreEqual(HttpStatusCode.OK, add.StatusCode, await add.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
        var repos = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual(1, repos.GetArrayLength());
        Assert.AreEqual("repo:sysmin", repos[0].GetProperty("matchTag").GetString());
        Assert.IsTrue(JsonDocument.Parse(await test.Content.ReadAsStringAsync()).RootElement.GetProperty("ok").GetBoolean());
    }

    [TestMethod]
    public async Task Finishing_ends_the_setup_session_and_the_link()
    {
        await using var app = await StartAsync();
        var token = SetupSessionTests.Tokens(app).Issue();
        var client = await SetupClient.SignInAsync(app, token);
        using var db = await client.SendAsync(HttpMethod.Put, "/api/setup/database", new { connectionString = ConnectionString });
        using var ado = await client.SendAsync(HttpMethod.Put, "/api/setup/azure-devops", new { organization = "myorg", project = "Portal", auth = "AzCli" });

        using var review = await client.SendAsync(HttpMethod.Get, "/api/setup/review");
        using var finish = await client.SendAsync(HttpMethod.Post, "/api/setup/finish");
        using var after = await client.SendAsync(HttpMethod.Get, "/api/setup/session");
        using var link = await SetupSessionTests.Client(app).GetAsync(new Uri($"/setup?token={token}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.OK, review.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, finish.StatusCode, await finish.Content.ReadAsStringAsync());
        Assert.IsTrue(_config.ContainsKey("Setup:CompletedAt"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, after.StatusCode, "the setup session is over");
        Assert.AreEqual(HttpStatusCode.NotFound, link.StatusCode, "the link is dead");
        Assert.IsFalse(SetupSessionTests.Tokens(app).Verify(token), "revoked");
    }

    public void Dispose() => _session.Dispose();

    private Task<WebApplication> StartAsync() => _session.StartAsync(services =>
    {
        services.AddSingleton<IConfigWriter>(new MemoryConfig(_config));
        services.AddSingleton<ISecrets>(new MemorySecrets(_secrets));
        services.AddSingleton<ISettingsAudit, NoAudit>();
        services.AddSingleton<IDatabaseProbe, OkDatabase>();
        services.AddSingleton<IAzureDevOpsProbe, OkAzureDevOps>();
        services.AddSingleton<IGitKey, MemoryGitKey>();
        services.AddSingleton<IClaudeProbe, OkClaude>();
        services.AddSingleton<IChatProbe, OkChat>();
        services.AddSingleton<Agentd.Application.Ports.IGitRemote, DevelopRemote>();
        services.AddSingleton<ISetupLink>(sp => sp.GetRequiredService<Setup.SetupToken>());
        services.AddSingleton<ISetupState>(new ConfigSetupState(_config));   // complete once Setup:CompletedAt is written
        services.AddSingleton(Options.Create(new JobOptions()));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SetupService>();
    });

    /// <summary>What the wizard does: open the link, fetch the setup antiforgery token, send both cookies.</summary>
    private sealed class SetupClient(HttpClient http, string cookies, string token)
    {
        public static async Task<SetupClient> SignInAsync(WebApplication app, string? link = null)
        {
            var session = await SetupSessionTests.ExchangeAsync(app, link ?? SetupSessionTests.Tokens(app).Issue());
            var http = SetupSessionTests.Client(app);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/setup/antiforgery", UriKind.Relative));
            request.Headers.Add("Cookie", session);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var antiforgery = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
            return new SetupClient(http, $"{session}; {antiforgery}", token);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, bool withToken = true)
        {
            using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Add("Cookie", cookies);
            if (withToken)
            {
                request.Headers.Add("X-XSRF-TOKEN", token);
            }

            return await http.SendAsync(request);
        }
    }

    private sealed class MemoryConfig(Dictionary<string, string?> values) : IConfigWriter
    {
        public string? Read(string key) => values.GetValueOrDefault(key);

        public Task SetAsync(IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken)
        {
            foreach (var (k, v) in changes)
            {
                values[k] = v;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ConfigSetupState(Dictionary<string, string?> config) : ISetupState
    {
        public bool IsComplete => config.ContainsKey("Setup:CompletedAt");
    }

    private sealed class MemorySecrets(Dictionary<string, string> values) : ISecrets
    {
        public SecretStatus Status(string key) => values.ContainsKey(key) ? new SecretStatus(true, DateTimeOffset.UnixEpoch, "setup") : SecretStatus.Missing;

        public string? TryGet(string key) => values.GetValueOrDefault(key);

        public void Store(string key, string value, string by) => values[key] = value;

        public bool Remove(string key) => values.Remove(key);
    }

    private sealed class NoAudit : ISettingsAudit
    {
        public Task RecordAsync(IReadOnlyList<SettingsChange> changes, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class OkDatabase : IDatabaseProbe
    {
        public string? Problem(string connectionString) => null;

        public Task<StepCheck> TestAsync(string connectionString, CancellationToken cancellationToken) => Task.FromResult(new StepCheck(true, "connected"));

        public Task<StepCheck> MigrateAsync(string connectionString, CancellationToken cancellationToken) => Task.FromResult(new StepCheck(true, "migrated"));
    }

    private sealed class OkAzureDevOps : IAzureDevOpsProbe
    {
        public Task<StepCheck> TestAsync(AzureDevOpsConnection connection, JobOptions jobs, CancellationToken cancellationToken) =>
            Task.FromResult(new StepCheck(true, $"signed in to {connection.Organization}"));
    }

    private sealed class MemoryGitKey : IGitKey
    {
        private GitKeyInfo? _key;

        public GitKeyInfo? Read() => _key;

        public Task<GitKeyInfo> GenerateAsync(string comment, CancellationToken cancellationToken) =>
            Task.FromResult(_key = new GitKeyInfo("/k/id_ed25519", "ssh-ed25519 AAAA agentd@test", "SHA256:abc"));

        public Task<StepCheck> TestAsync(string url, CancellationToken cancellationToken) => Task.FromResult(new StepCheck(true, "reached"));
    }

    private sealed class OkClaude : IClaudeProbe
    {
        public Task<ClaudeLogin> StatusAsync(CancellationToken cancellationToken) => Task.FromResult(new ClaudeLogin(true, "2.1.300", true, "claude.ai", "max"));

        public Task<StepCheck> TestAsync(string? token, CancellationToken cancellationToken) => Task.FromResult(new StepCheck(true, "answered"));
    }

    private sealed class OkChat : IChatProbe
    {
        public Task<StepCheck> TestAsync(ChatConnection connection, CancellationToken cancellationToken) => Task.FromResult(new StepCheck(true, "posted"));
    }

    private sealed class DevelopRemote : Agentd.Application.Ports.IGitRemote
    {
        public Task<string> GetDefaultBranchAsync(string remoteUrl, CancellationToken cancellationToken) => Task.FromResult("develop");
    }
}
