using System.Net;
using System.Text;
using System.Text.Json;
using Agentd.Application.AzureDevOps;
using Agentd.Application.Events;
using Agentd.Application.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class AdoConnectEndpointTests
{
    private static readonly Guid s_dev = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly Delegation _delegation = new();
    private readonly Connections _store = new();

    [TestMethod]
    public async Task Connect_goes_to_microsoft_and_the_callback_stores_the_person_then_lists_and_disconnects_it()
    {
        await using var app = await StartAsync();
        var http = app.GetTestClient();

        using var connect = await http.GetAsync(new Uri("/bff/ado/connect", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.Redirect, connect.StatusCode);
        Assert.AreEqual("https://login.example/authorize", connect.Headers.Location!.ToString());
        var cookie = connect.Headers.GetValues("Set-Cookie").Single();
        StringAssert.Contains(cookie, "httponly", StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(cookie, "samesite=lax", StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual("http://localhost/bff/ado/callback", _delegation.Redirect!.ToString(), "the callback the app registration must list");

        using var callback = await SendAsync(http, $"/bff/ado/callback?code=the-code&state={_delegation.State}", cookie.Split(';')[0]);
        Assert.AreEqual("/settings?ado=connected", callback.Headers.Location!.ToString());
        Assert.AreEqual(("local", "Dev One"), (_store.Rows.Single().WebLogin, _store.Rows.Single().DisplayName));

        var mine = await http.GetStringAsync(new Uri("/api/me/ado-connections", UriKind.Relative));
        var body = JsonDocument.Parse(mine).RootElement;
        Assert.IsTrue(body.GetProperty("available").GetBoolean());
        Assert.AreEqual("dev.one@example.com", body.GetProperty("connections")[0].GetProperty("uniqueName").GetString());
        Assert.DoesNotContain("SECRET", mine);

        var antiforgery = await new AntiforgeryClient(app).InitAsync();
        using var disconnect = await antiforgery.SendAsync(HttpMethod.Delete, $"/api/me/ado-connections/{s_dev}");
        using var again = await antiforgery.SendAsync(HttpMethod.Delete, $"/api/me/ado-connections/{s_dev}");
        Assert.AreEqual((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (disconnect.StatusCode, again.StatusCode));
    }

    [TestMethod]
    public async Task A_callback_without_its_cookie_or_cancelled_at_microsoft_stores_nothing_and_says_why()
    {
        await using var app = await StartAsync();
        var http = app.GetTestClient();

        using var forged = await http.GetAsync(new Uri("/bff/ado/callback?code=c&state=guessed", UriKind.Relative));
        using var cancelled = await http.GetAsync(new Uri("/bff/ado/callback?error=access_denied&state=x", UriKind.Relative));

        StringAssert.StartsWith(forged.Headers.Location!.ToString(), "/settings?ado=error&reason=This%20sign-in%20didn");
        StringAssert.Contains(Uri.UnescapeDataString(cancelled.Headers.Location!.ToString()), "You cancelled the sign-in");
        Assert.IsEmpty(_store.Rows);
    }

    [TestMethod]
    public async Task A_pat_is_saved_write_only_and_the_commit_author_changes_only_for_its_owner()
    {
        await using var app = await StartAsync();
        var antiforgery = await new AntiforgeryClient(app).InitAsync();

        using var added = await antiforgery.SendAsync(HttpMethod.Post, "/api/me/ado-connections/pat", new { token = "pat-SECRET", commitName = "Dev O.", commitEmail = "" });
        var body = await added.Content.ReadAsStringAsync();
        using var bad = await antiforgery.SendAsync(HttpMethod.Post, "/api/me/ado-connections/pat", new { token = "" });
        using var author = await antiforgery.SendAsync(HttpMethod.Put, $"/api/me/ado-connections/{s_dev}/commit-author", new { name = "", email = "dev@work.example" });
        using var unknown = await antiforgery.SendAsync(HttpMethod.Put, $"/api/me/ado-connections/{Guid.NewGuid()}/commit-author", new { name = "x" });

        Assert.AreEqual((HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.OK, HttpStatusCode.NotFound), (added.StatusCode, bad.StatusCode, author.StatusCode, unknown.StatusCode));
        var vm = JsonDocument.Parse(body).RootElement;
        Assert.AreEqual(("Pat", "Dev O.", "dev.one@example.com"), (vm.GetProperty("kind").GetString(), vm.GetProperty("authorName").GetString(), vm.GetProperty("authorEmail").GetString()));
        Assert.DoesNotContain("SECRET", body);
        Assert.AreEqual(("Dev One", "dev@work.example"), (_store.Rows.Single().Author!.Name, _store.Rows.Single().Author!.Email));
    }

    [TestMethod]
    public async Task Without_the_entra_app_connect_says_its_unavailable()
    {
        _delegation.Configured = false;
        await using var app = await StartAsync();

        using var connect = await app.GetTestClient().GetAsync(new Uri("/bff/ado/connect", UriKind.Relative));

        Assert.AreEqual("/settings?ado=unavailable", connect.Headers.Location!.ToString());
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient http, string url, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Add("Cookie", cookie);
        return http.SendAsync(request);
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton<ILiveEvents>(new EventHub(NullLogger<EventHub>.Instance));
        builder.Services.AddSingleton<IAdoDelegation>(_delegation);
        builder.Services.AddSingleton<IAdoUserConnections>(_store);
        builder.Services.AddSingleton<ITokenProtector, Protector>();
        builder.Services.AddSingleton<IAdoPatCheck>(new Pats());
        builder.Services.AddSingleton<AdoConnections>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private sealed class Delegation : IAdoDelegation
    {
        public bool Configured { get; set; } = true;

        public string? State { get; private set; }

        public Uri? Redirect { get; private set; }

        public bool IsConfigured => Configured;

        public Uri AuthorizeUrl(string state, string codeChallenge, Uri redirectUri)
        {
            (State, Redirect) = (state, redirectUri);
            return new Uri("https://login.example/authorize");
        }

        public Task<DelegatedSignIn> RedeemAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken cancellationToken) =>
            Task.FromResult(new DelegatedSignIn(s_dev, "dev.one@example.com", "Dev One", "refresh-SECRET"));

        public Task<DelegatedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Pats : IAdoPatCheck
    {
        public Task<DelegatedSignIn> WhoAsync(string pat, CancellationToken cancellationToken) =>
            Task.FromResult(new DelegatedSignIn(s_dev, "dev.one@example.com", "Dev One", pat));
    }

    private sealed class Protector : ITokenProtector
    {
        public byte[] Protect(string token) => Encoding.UTF8.GetBytes(new string(token.Reverse().ToArray()));

        public string? Unprotect(byte[] protectedToken) => new(Encoding.UTF8.GetString(protectedToken).Reverse().ToArray());
    }

    private sealed class Connections : IAdoUserConnections
    {
        public List<AdoUserConnection> Rows { get; } = [];

        public Task UpsertAsync(Guid identityId, string uniqueName, string displayName, string webLogin, byte[] refreshToken, AdoConnectionKind kind, CancellationToken cancellationToken)
        {
            Rows.Add(new AdoUserConnection(identityId, uniqueName, displayName, webLogin, refreshToken, false, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, kind));
            return Task.CompletedTask;
        }

        public Task<bool> SetCommitAuthorAsync(Guid identityId, string webLogin, string? name, string? email, CancellationToken cancellationToken)
        {
            var i = Rows.FindIndex(r => r.IdentityId == identityId && r.WebLogin == webLogin);
            if (i >= 0)
            {
                Rows[i] = Rows[i] with { CommitName = name, CommitEmail = email };
            }

            return Task.FromResult(i >= 0);
        }

        public Task<AdoUserConnection?> FindByIdentityAsync(Guid identityId, CancellationToken cancellationToken) => Task.FromResult(Rows.FirstOrDefault(r => r.IdentityId == identityId));

        public Task<AdoUserConnection?> FindByUniqueNameAsync(string uniqueName, CancellationToken cancellationToken) => Task.FromResult<AdoUserConnection?>(null);

        public Task<IReadOnlyList<AdoUserConnection>> ListByWebLoginAsync(string webLogin, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AdoUserConnection>>([.. Rows.Where(r => r.WebLogin == webLogin)]);

        public Task<bool> StoreTokenAsync(Guid identityId, byte[] refreshToken, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task MarkFailedAsync(Guid identityId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> DeleteAsync(Guid identityId, string webLogin, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.RemoveAll(r => r.IdentityId == identityId && r.WebLogin == webLogin) > 0);
    }
}
