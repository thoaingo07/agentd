using System.Net;
using System.Text.Json;
using Agentd.Application.Events;
using Agentd.Application.Setup;
using Agentd.Bff.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class SetupSessionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("agentd-setup-").FullName;
    private readonly FakeSetupState _state = new();

    [TestMethod]
    public async Task The_link_becomes_a_strict_http_only_session_cookie_and_leaves_the_address_bar()
    {
        await using var app = await StartAsync();
        var token = Tokens(app).Issue();

        using var response = await Client(app).GetAsync(new Uri($"/setup?token={token}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/setup", response.Headers.Location?.OriginalString);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(SetupSession.CookieName + "=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.DoesNotContain("expires=", cookie, "a browser-session cookie; the 30 minutes (sliding) are in the ticket");
        Assert.DoesNotContain(token.ToLowerInvariant(), cookie);
    }

    [TestMethod]
    public async Task The_session_opens_the_setup_api_for_thirty_minutes()
    {
        await using var app = await StartAsync();
        var cookie = await ExchangeAsync(app, Tokens(app).Issue());

        using var session = await GetAsync(app, "/api/setup/session", cookie);

        Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        var expires = JsonDocument.Parse(await session.Content.ReadAsStringAsync()).RootElement.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.IsTrue(expires > DateTimeOffset.UtcNow.AddMinutes(25) && expires <= DateTimeOffset.UtcNow.AddMinutes(31), $"expires {expires:O}");
        using var antiforgery = await GetAsync(app, "/api/setup/antiforgery", cookie);
        Assert.AreEqual(HttpStatusCode.OK, antiforgery.StatusCode);
    }

    [TestMethod]
    [DataRow("wrong")]
    [DataRow("")]
    public async Task A_wrong_token_gets_no_session(string token)
    {
        await using var app = await StartAsync();
        Tokens(app).Issue();

        using var response = await Client(app).GetAsync(new Uri($"/setup?token={token}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.IsFalse(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(SetupSession.CookieName, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task A_new_link_replaces_the_old_one()
    {
        await using var app = await StartAsync();
        var old = Tokens(app).Issue();
        var current = Tokens(app).Issue();

        using var withOld = await Client(app).GetAsync(new Uri($"/setup?token={old}", UriKind.Relative));
        using var withCurrent = await Client(app).GetAsync(new Uri($"/setup?token={current}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.Forbidden, withOld.StatusCode);
        Assert.AreEqual(HttpStatusCode.Redirect, withCurrent.StatusCode);
    }

    [TestMethod]
    public async Task The_setup_api_needs_the_setup_session_even_for_the_local_admin()
    {
        await using var app = await StartAsync();

        using var response = await Client(app).GetAsync(new Uri("/api/setup/session", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task The_setup_session_opens_nothing_else()
    {
        await using var app = await StartAsync();
        var cookie = await ExchangeAsync(app, Tokens(app).Issue());

        // Forwarded: not the loopback user, so only the setup cookie could sign this request in.
        using var user = await GetAsync(app, "/bff/user", cookie, forwarded: true);
        using var setup = await GetAsync(app, "/api/setup/session", cookie, forwarded: true);

        Assert.AreEqual(HttpStatusCode.Unauthorized, user.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, setup.StatusCode);
    }

    [TestMethod]
    public async Task Completing_setup_kills_the_link_and_every_session()
    {
        await using var app = await StartAsync();
        var token = Tokens(app).Issue();
        var cookie = await ExchangeAsync(app, token);

        _state.IsComplete = true;
        using var link = await Client(app).GetAsync(new Uri($"/setup?token={token}", UriKind.Relative));
        using var session = await GetAsync(app, "/api/setup/session", cookie);

        Assert.AreEqual(HttpStatusCode.NotFound, link.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    [TestMethod]
    public async Task The_page_without_a_token_says_how_to_get_one()
    {
        await using var app = await StartAsync();

        using var response = await Client(app).GetAsync(new Uri("/setup", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("agentd setup-link", await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    internal static SetupToken Tokens(WebApplication app) => app.Services.GetRequiredService<SetupToken>();

    internal static HttpClient Client(WebApplication app) => app.GetTestServer().CreateClient();   // no redirects followed

    internal static async Task<string> ExchangeAsync(WebApplication app, string token)
    {
        using var response = await Client(app).GetAsync(new Uri($"/setup?token={token}", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(SetupSession.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];
    }

    private static async Task<HttpResponseMessage> GetAsync(WebApplication app, string url, string cookie, bool forwarded = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Add("Cookie", cookie);
        if (forwarded)
        {
            request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        }

        return await Client(app).SendAsync(request);
    }

    internal async Task<WebApplication> StartAsync(Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton<ILiveEvents>(new EventHub(NullLogger<EventHub>.Instance));
        builder.Services.AddSingleton<ISetupState>(_state);
        builder.Services.AddSingleton(new SetupToken(Path.Combine(_dir, "run", "setup-token")));
        services?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private sealed class FakeSetupState : ISetupState
    {
        public bool IsComplete { get; set; }
    }
}
