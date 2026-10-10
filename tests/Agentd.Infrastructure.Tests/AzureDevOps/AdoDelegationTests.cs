using System.Net;
using System.Web;
using Agentd.Infrastructure.AzureDevOps;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Tests.AzureDevOps;

[TestClass]
public sealed class AdoDelegationTests : IDisposable
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";
    private static readonly Uri s_callback = new("https://agentd.example/bff/ado/callback");
    private readonly FakeAdo _http = new();

    [TestMethod]
    public void The_sign_in_asks_for_azure_devops_on_their_behalf_with_pkce()
    {
        var url = Delegation().AuthorizeUrl("st", "ch", s_callback);
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.AreEqual($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize", url.GetLeftPart(UriPartial.Path));
        Assert.AreEqual((Client, "code", s_callback.ToString(), "st", "ch", "S256"),
            (query["client_id"], query["response_type"], query["redirect_uri"], query["state"], query["code_challenge"], query["code_challenge_method"]));
        Assert.AreEqual(AdoDelegation.Scopes, query["scope"]);
        Assert.IsNull(query["client_secret"], "the secret never goes to the browser");
    }

    [TestMethod]
    public async Task Redeeming_the_code_learns_who_signed_in_from_azure_devops()
    {
        _http.On(HttpMethod.Post, $"/{Tenant}/oauth2/v2.0/token", HttpStatusCode.OK, """{"access_token":"at","refresh_token":"rt","expires_in":3600}""")
            .On(HttpMethod.Get, "/myorg/_apis/connectionData", HttpStatusCode.OK, """
                {"authenticatedUser":{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","providerDisplayName":"Dev One","properties":{"Account":{"$type":"System.String","$value":"dev.one@example.com"}}}}
                """);

        var signIn = await Delegation().RedeemAsync("the-code", "the-verifier", s_callback, default);

        Assert.AreEqual((Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "dev.one@example.com", "Dev One", "rt"), (signIn.IdentityId, signIn.UniqueName, signIn.DisplayName, signIn.RefreshToken));
        var form = HttpUtility.ParseQueryString(_http.Requests[0].Body!);
        Assert.AreEqual(("authorization_code", "the-code", "the-verifier", "shh", s_callback.ToString()),
            (form["grant_type"], form["code"], form["code_verifier"], form["client_secret"], form["redirect_uri"]));
        Assert.AreEqual("Bearer at", _http.Requests[1].Auth, "Azure DevOps sees the person's token");
        Assert.DoesNotContain("rt", signIn.ToString());
    }

    [TestMethod]
    public async Task Entras_reason_and_a_non_member_account_are_reported_plainly()
    {
        _http.On(HttpMethod.Post, $"/{Tenant}/oauth2/v2.0/token", HttpStatusCode.BadRequest, """
            {"error":"invalid_grant","error_description":"AADSTS65001: The user or administrator has not consented to use the application.\r\nTrace ID: x"}
            """);
        var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Delegation().RedeemAsync("c", "v", s_callback, default));

        _http.On(HttpMethod.Post, $"/{Tenant}/oauth2/v2.0/token", HttpStatusCode.OK, """{"access_token":"at","refresh_token":"rt"}""")
            .On(HttpMethod.Get, "/myorg/_apis/connectionData", HttpStatusCode.OK, """{"authenticatedUser":{"id":"00000000-0000-0000-0000-000000000000"}}""");
        var outsider = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Delegation().RedeemAsync("c", "v", s_callback, default));

        Assert.AreEqual("AADSTS65001: The user or administrator has not consented to use the application.", refused.Message);
        Assert.Contains("member of the organization", outsider.Message);
    }

    [TestMethod]
    public async Task A_refresh_rotates_the_token_and_tells_refused_from_unreachable()
    {
        _http.On(HttpMethod.Post, $"/{Tenant}/oauth2/v2.0/token", HttpStatusCode.OK, """{"access_token":"at2","refresh_token":"rt2","expires_in":4000}""");
        var token = await Delegation().RefreshAsync("rt1", default);
        var form = HttpUtility.ParseQueryString(_http.Requests[0].Body!);

        _http.On(HttpMethod.Post, $"/{Tenant}/oauth2/v2.0/token", HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"AADSTS700082: The refresh token has expired.\r\nTrace"}""");
        var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Delegation().RefreshAsync("rt2", default));
        _http.On(HttpMethod.Post, $"/{Tenant}/oauth2/v2.0/token", HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => Delegation().RefreshAsync("rt2", default));

        Assert.AreEqual(("at2", "rt2", TimeSpan.FromSeconds(4000)), (token.AccessToken, token.RefreshToken, token.ExpiresIn));
        Assert.AreEqual(("refresh_token", "rt1", "shh"), (form["grant_type"], form["refresh_token"], form["client_secret"]));
        Assert.AreEqual("AADSTS700082: The refresh token has expired.", refused.Message);
        Assert.DoesNotContain("at2", token.ToString());
    }

    [TestMethod]
    public async Task Inside_an_acting_scope_requests_carry_the_persons_token_and_never_fall_back()
    {
        var actor = new Application.AzureDevOps.AdoActor();
        var store = new Store();
        var delegation = new Refresher();
        var tokens = new Application.AzureDevOps.AdoUserTokens(store, new Protector(), delegation, new Clock());
        var provider = new ActingAsAuthProvider(new Own(), actor, tokens);

        var own = await provider.GetAsync(false, default);
        string person;
        using (actor.Begin(store.Id))
        {
            person = (await provider.GetAsync(false, default)).ToString();
        }

        store.Failed = true;
        using (actor.Begin(store.Id))
        {
            await Assert.ThrowsExactlyAsync<AdoException>(async () => await provider.GetAsync(true, default));
        }

        Assert.AreEqual(("Basic agentd", "Bearer person-token"), (own.ToString(), person));
    }

    [TestMethod]
    public async Task A_person_who_connected_with_a_pat_acts_with_it_as_basic_auth()
    {
        var actor = new Application.AzureDevOps.AdoActor();
        var store = new Store { Kind = Application.Ports.AdoConnectionKind.Pat };
        var tokens = new Application.AzureDevOps.AdoUserTokens(store, new Protector(), new Refresher(), new Clock());
        var provider = new ActingAsAuthProvider(new Own(), actor, tokens);

        using (actor.Begin(store.Id))
        {
            var header = await provider.GetAsync(false, default);

            Assert.AreEqual(("Basic", Convert.ToBase64String(":the-pat"u8.ToArray())), (header.Scheme, header.Parameter));
        }
    }

    [TestMethod]
    public async Task A_pat_is_checked_with_connection_data_and_an_unknown_one_is_refused()
    {
        _http.On(HttpMethod.Get, "/myorg/_apis/connectionData", HttpStatusCode.OK, """
            {"authenticatedUser":{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","providerDisplayName":"Dev One","properties":{"Account":{"$type":"System.String","$value":"dev.one@example.com"}}}}
            """);
        var who = await Pats().WhoAsync("the-pat", default);
        var auth = _http.Requests.Single().Auth;

        _http.On(HttpMethod.Get, "/myorg/_apis/connectionData", HttpStatusCode.OK, """
            {"authenticatedUser":{"id":"aa44a7d7-0000-0000-0000-000000000000","providerDisplayName":"Anonymous","properties":{}}}
            """);
        var anonymous = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Pats().WhoAsync("unknown", default));

        Assert.AreEqual((Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "dev.one@example.com", "Dev One"), (who.IdentityId, who.UniqueName, who.DisplayName));
        Assert.AreEqual("Basic " + Convert.ToBase64String(":the-pat"u8.ToArray()), auth);
        Assert.Contains("Code (Read & write)", anonymous.Message);
        Assert.DoesNotContain("the-pat", who.ToString());
    }

    [TestMethod]
    public void Its_configured_only_with_the_apps_tenant_client_and_secret() =>
        Assert.IsFalse(new AdoDelegation(new HttpClient(), Options.Create(new AzureDevOpsOptions { TenantId = Tenant, ClientId = Client })).IsConfigured);

    public void Dispose() => _http.Dispose();

    private sealed class Own : IAdoAuthProvider
    {
        public ValueTask<System.Net.Http.Headers.AuthenticationHeaderValue> GetAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "agentd"));
    }

    private sealed class Clock : Domain.Common.IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class Protector : Application.Ports.ITokenProtector
    {
        public byte[] Protect(string token) => System.Text.Encoding.UTF8.GetBytes(token);

        public string? Unprotect(byte[] protectedToken) => System.Text.Encoding.UTF8.GetString(protectedToken);
    }

    private sealed class Refresher : Application.Ports.IAdoDelegation
    {
        public bool IsConfigured => true;

        public Uri AuthorizeUrl(string state, string codeChallenge, Uri redirectUri) => throw new NotSupportedException();

        public Task<Application.Ports.DelegatedSignIn> RedeemAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Application.Ports.DelegatedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
            Task.FromResult(new Application.Ports.DelegatedToken("person-token", null, TimeSpan.FromHours(1)));
    }

    /// <summary>One connected person; <see cref="Failed"/> makes their sign-in unusable.</summary>
    private sealed class Store : Application.Ports.IAdoUserConnections
    {
        public Guid Id { get; } = Guid.NewGuid();

        public bool Failed { get; set; }

        public Application.Ports.AdoConnectionKind Kind { get; set; }

        public Task<Application.Ports.AdoUserConnection?> FindByIdentityAsync(Guid identityId, CancellationToken cancellationToken) =>
            Task.FromResult<Application.Ports.AdoUserConnection?>(identityId == Id ? new(Id, "dev@example.com", "Dev", "dev@example.com", "the-pat"u8.ToArray(), Failed, null, default, default, Kind) : null);

        public Task UpsertAsync(Guid identityId, string uniqueName, string displayName, string webLogin, byte[] refreshToken, Application.Ports.AdoConnectionKind kind, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> SetCommitAuthorAsync(Guid identityId, string webLogin, string? name, string? email, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<Application.Ports.AdoUserConnection?> FindByUniqueNameAsync(string uniqueName, CancellationToken cancellationToken) => Task.FromResult<Application.Ports.AdoUserConnection?>(null);

        public Task<IReadOnlyList<Application.Ports.AdoUserConnection>> ListByWebLoginAsync(string webLogin, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Application.Ports.AdoUserConnection>>([]);

        public Task<bool> StoreTokenAsync(Guid identityId, byte[] refreshToken, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task MarkFailedAsync(Guid identityId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> DeleteAsync(Guid identityId, string webLogin, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private AdoPatCheck Pats() => new(new HttpClient(_http, disposeHandler: false), Options.Create(new AzureDevOpsOptions { Organization = "myorg" }));

    private AdoDelegation Delegation() => new(new HttpClient(_http, disposeHandler: false),
        Options.Create(new AzureDevOpsOptions { Organization = "myorg", TenantId = Tenant, ClientId = Client, ClientSecret = "shh" }));
}
