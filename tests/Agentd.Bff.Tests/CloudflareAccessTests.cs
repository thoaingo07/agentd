using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Agentd.Application.Events;
using Agentd.Bff.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class CloudflareAccessTests
{
    private const string Team = "agentd-test.cloudflareaccess.com";
    private const string Audience = "aud-tag-123";

    [TestMethod]
    public async Task A_valid_token_signs_in_as_its_email()
    {
        await using var host = await CloudflareHost.StartAsync();

        var user = await host.GetUserAsync(host.Token("dev@example.com"));

        Assert.AreEqual(HttpStatusCode.OK, user.Status);
        Assert.AreEqual("dev@example.com", user.Json!.Value.GetProperty("name").GetString());
        Assert.AreEqual("User", user.Json!.Value.GetProperty("roles")[0].GetString());
        Assert.AreEqual("cloudflare", user.Json!.Value.GetProperty("provider").GetString());
    }

    [TestMethod]
    public async Task Settings_are_for_admins_only()
    {
        await using var host = await CloudflareHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/settings/database", UriKind.Relative));
        request.Headers.Add(CloudflareAccessAuthenticationHandler.HeaderName, host.Token("dev@example.com"));

        using var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, "a signed-in User isn't an Admin");
    }

    [TestMethod]
    public async Task Admin_emails_get_the_admin_role()
    {
        await using var host = await CloudflareHost.StartAsync();

        var user = await host.GetUserAsync(host.Token("Owner@Example.com"));

        Assert.AreEqual("Admin", user.Json!.Value.GetProperty("roles")[0].GetString());
    }

    [TestMethod]
    [DataRow("audience")]
    [DataRow("issuer")]
    [DataRow("expired")]
    [DataRow("signature")]
    [DataRow("no-email")]
    [DataRow("not-allowed")]
    public async Task Bad_tokens_are_401(string defect)
    {
        await using var host = await CloudflareHost.StartAsync();
        var token = defect switch
        {
            "audience" => host.Token("dev@example.com", audience: "someone-else"),
            "issuer" => host.Token("dev@example.com", issuer: "https://evil.cloudflareaccess.com"),
            "expired" => host.Token("dev@example.com", expires: DateTime.UtcNow.AddMinutes(-5)),
            "signature" => host.Token("dev@example.com", key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = host.KeyId }),
            "no-email" => host.Token(null),
            _ => host.Token("stranger@example.com"),
        };

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetUserAsync(token)).Status);
    }

    [TestMethod]
    public async Task Localhost_without_a_token_is_not_trusted()
    {
        await using var host = await CloudflareHost.StartAsync();

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetUserAsync(null)).Status);
    }

    [TestMethod]
    public async Task Rotated_keys_are_fetched_once_on_an_unknown_key_id()
    {
        await using var host = await CloudflareHost.StartAsync();
        Assert.AreEqual(HttpStatusCode.OK, (await host.GetUserAsync(host.Token("dev@example.com"))).Status);
        Assert.AreEqual(1, host.CertFetches);

        host.Time.Advance(TimeSpan.FromMinutes(2));
        host.RotateKey();
        Assert.AreEqual(HttpStatusCode.OK, (await host.GetUserAsync(host.Token("dev@example.com"))).Status);

        Assert.AreEqual(2, host.CertFetches);
    }

    [TestMethod]
    public async Task Behind_the_tunnel_the_daemon_sees_https_and_uses_the_secure_cookie()
    {
        await using var host = await CloudflareHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/antiforgery");
        request.Headers.Add(CloudflareAccessAuthenticationHandler.HeaderName, host.Token("dev@example.com"));
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");

        using var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        StringAssert.StartsWith(cookie, "__Host-agentd.af=");
        StringAssert.Contains(cookie.ToLowerInvariant(), "secure");
    }

    private sealed record UserResult(HttpStatusCode Status, JsonElement? Json);

    private sealed class CloudflareHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly Certs _certs;

        private CloudflareHost(WebApplication app, Certs certs, ManualTime time)
        {
            _app = app;
            _certs = certs;
            Time = time;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public ManualTime Time { get; }

        public int CertFetches => _certs.Fetches;

        public string KeyId => _certs.Key.KeyId;

        public static async Task<CloudflareHost> StartAsync()
        {
            var certs = new Certs();
            var time = new ManualTime();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration["Agentd:Auth:Mode"] = "CloudflareAccess";
            builder.Configuration["Agentd:Auth:CloudflareAccess:TeamDomain"] = Team;
            builder.Configuration["Agentd:Auth:CloudflareAccess:Audience"] = Audience;
            builder.Configuration["Agentd:Auth:CloudflareAccess:AllowedEmails:0"] = "dev@example.com";
            builder.Configuration["Agentd:Auth:CloudflareAccess:AllowedEmails:1"] = "owner@example.com";
            builder.Configuration["Agentd:Auth:CloudflareAccess:AdminEmails:0"] = "owner@example.com";
            builder.Services.AddBff();
            builder.Services.AddSingleton<TimeProvider>(time);
            builder.Services.AddHttpClient(CloudflareAccessKeys.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => certs);
            builder.Services.AddSingleton<ILiveEvents>(new EventHub(NullLogger<EventHub>.Instance));
            var app = builder.Build();
            app.UseBffForwardedHeaders();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapBff();
            await app.StartAsync();
            return new CloudflareHost(app, certs, time);
        }

        public void RotateKey() => _certs.Rotate();

        public string Token(string? email, string audience = Audience, string issuer = "https://" + Team, DateTime? expires = null, SecurityKey? key = null)
        {
            var claims = new Dictionary<string, object> { ["sub"] = "user-1" };
            if (email is not null)
            {
                claims["email"] = email;
            }

            var exp = expires ?? DateTime.UtcNow.AddMinutes(10);
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Claims = claims,
                IssuedAt = exp.AddMinutes(-20),
                NotBefore = exp.AddMinutes(-20),
                Expires = exp,
                SigningCredentials = new SigningCredentials(key ?? _certs.Key, SecurityAlgorithms.RsaSha256),
            });
        }

        public async Task<UserResult> GetUserAsync(string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/user");
            if (token is not null)
            {
                request.Headers.Add(CloudflareAccessAuthenticationHandler.HeaderName, token);
            }

            using var response = await Client.SendAsync(request);
            return new(response.StatusCode, response.IsSuccessStatusCode ? JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone() : null);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    /// <summary>Stands in for https://&lt;team&gt;/cdn-cgi/access/certs.</summary>
    private sealed class Certs : HttpMessageHandler
    {
        public Certs() => Rotate();

        public RsaSecurityKey Key { get; private set; } = null!;

        public int Fetches { get; private set; }

        public void Rotate() => Key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = Guid.NewGuid().ToString("N") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual($"https://{Team}/cdn-cgi/access/certs", request.RequestUri!.ToString());
            Fetches++;
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(Key);
            var json = JsonSerializer.Serialize(new { keys = new[] { new { kid = jwk.Kid, kty = jwk.Kty, alg = "RS256", use = "sig", n = jwk.N, e = jwk.E } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
