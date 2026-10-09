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
    public void Its_configured_only_with_the_apps_tenant_client_and_secret() =>
        Assert.IsFalse(new AdoDelegation(new HttpClient(), Options.Create(new AzureDevOpsOptions { TenantId = Tenant, ClientId = Client })).IsConfigured);

    public void Dispose() => _http.Dispose();

    private AdoDelegation Delegation() => new(new HttpClient(_http, disposeHandler: false),
        Options.Create(new AzureDevOpsOptions { Organization = "myorg", TenantId = Tenant, ClientId = Client, ClientSecret = "shh" }));
}
