using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentd.Application.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class SetupEndpointTests : IDisposable
{
    private const string ConnectionString = "Host=db;Username=agentd;Password=pw-SECRET;Database=agentd";

    private readonly SetupSessionTests _session = new();
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    [TestMethod]
    public async Task Secrets_go_in_but_never_come_back_out()
    {
        await using var app = await StartAsync();
        var client = await SetupClient.SignInAsync(app);

        using var saveDb = await client.SendAsync(HttpMethod.Put, "/api/setup/database", new { connectionString = ConnectionString });
        using var db = await client.SendAsync(HttpMethod.Get, "/api/setup/database");
        using var test = await client.SendAsync(HttpMethod.Post, "/api/setup/database/test", new { connectionString = (string?)null });
        using var migrate = await client.SendAsync(HttpMethod.Post, "/api/setup/database/migrate");

        foreach (var response in new[] { saveDb, db, test, migrate })
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SECRET", body);
        }

        Assert.AreEqual(ConnectionString, _secrets[SetupService.ConnectionStringSecret]);
        Assert.IsTrue(JsonDocument.Parse(await db.Content.ReadAsStringAsync()).RootElement.GetProperty("connectionString").GetProperty("set").GetBoolean());
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

        using var empty = await client.SendAsync(HttpMethod.Put, "/api/setup/database", new { connectionString = " " });
        using var tooLong = await client.SendAsync(HttpMethod.Put, "/api/setup/database", new { connectionString = new string('x', 5000) });

        Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("connection string", await empty.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    public void Dispose() => _session.Dispose();

    private Task<WebApplication> StartAsync() => _session.StartAsync(services =>
    {
        services.AddSingleton<ISecrets>(new MemorySecrets(_secrets));
        services.AddSingleton<ISettingsAudit, NoAudit>();
        services.AddSingleton<IDatabaseProbe, OkDatabase>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SetupService>();
    });

    /// <summary>What the wizard does: open the link, fetch the setup antiforgery token, send both cookies.</summary>
    private sealed class SetupClient(HttpClient http, string cookies, string token)
    {
        public static async Task<SetupClient> SignInAsync(WebApplication app)
        {
            var session = await SetupSessionTests.ExchangeAsync(app, SetupSessionTests.Tokens(app).Issue());
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
}
