using System.Net;
using System.Text.Json.Nodes;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;
using Agentd.Infrastructure.AzureDevOps;
using Azure.Core;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Tests.AzureDevOps;

[TestClass]
public sealed class PullRequestsAndAuthTests
{
    private static readonly Repository s_repo = Repository.From(RemoteUrl.Parse("git@erm-azdo:v3/ermsystem/Portal/sysmin").Value!, null, "develop", "repo:sysmin", []);
    private const string PrPath = "/ermsystem/Portal/_apis/git/repositories/sysmin/pullrequests";

    [TestMethod]
    public async Task Create_sends_refs_title_and_work_item_link_and_returns_the_web_url()
    {
        var ado = new FakeAdo().On(HttpMethod.Post, PrPath, HttpStatusCode.Created, """{ "pullRequestId": 77 }""");

        var pr = await new AzureDevOpsPullRequests(ado.Client()).CreateAsync(
            s_repo, BranchName.From("ai/1234-fix"), "develop", "Fix login", new string('x', 5000), WorkItemId.From(1234), default);

        Assert.AreEqual("https://dev.azure.com/ermsystem/Portal/_git/sysmin/pullrequest/77", pr.Url.ToString());
        var body = JsonNode.Parse(ado.Requests.Single().Body!)!;
        Assert.AreEqual("refs/heads/ai/1234-fix", body["sourceRefName"]!.GetValue<string>());
        Assert.AreEqual("refs/heads/develop", body["targetRefName"]!.GetValue<string>());
        Assert.AreEqual("1234", body["workItemRefs"]![0]!["id"]!.GetValue<string>());
        Assert.AreEqual(4000, body["description"]!.GetValue<string>().Length);
    }

    [TestMethod]
    public async Task Find_returns_the_active_pr_for_the_branch_or_null()
    {
        var found = new FakeAdo().On(HttpMethod.Get, PrPath, HttpStatusCode.OK, """{ "value": [ { "pullRequestId": 5 } ] }""");
        var none = new FakeAdo().On(HttpMethod.Get, PrPath, HttpStatusCode.OK, """{ "value": [] }""");

        Assert.AreEqual(5, (await new AzureDevOpsPullRequests(found.Client()).FindOpenAsync(s_repo, BranchName.From("ai/1-x"), default))!.Id);
        Assert.IsNull(await new AzureDevOpsPullRequests(none.Client()).FindOpenAsync(s_repo, BranchName.From("ai/1-x"), default));
        StringAssert.Contains(found.Requests.Single().Url, "searchCriteria.sourceRefName=refs%2Fheads%2Fai%2F1-x");
    }

    [TestMethod]
    public async Task Pat_is_sent_as_basic_auth_with_an_empty_user()
    {
        var auth = new PatAuthProvider(Options.Create(new AzureDevOpsOptions { Pat = "secret-pat" }));

        var header = await auth.GetAsync(false, default);

        Assert.AreEqual("Basic", header.Scheme);
        Assert.AreEqual(":secret-pat", System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(header.Parameter!)));
    }

    [TestMethod]
    public async Task A_401_refreshes_the_az_cli_token_once_and_retries()
    {
        var credential = new CountingCredential();
        using var provider = new AzCliAuthProvider(credential);
        var calls = 0;
        using var handler = new AdoAuthHandler(provider) { InnerHandler = new Responder(req => ++calls == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK) };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/") };

        using var response = await http.GetAsync(new Uri("ermsystem/_apis/projects", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, credential.Calls, "initial token + forced refresh");
    }

    [TestMethod]
    public async Task Requests_carry_the_msa_passthrough_and_no_redirect_headers()
    {
        HttpRequestMessage? seen = null;
        using var provider = new AzCliAuthProvider(new CountingCredential());
        using var handler = new AdoAuthHandler(provider) { InnerHandler = new Responder(req => { seen = req; return HttpStatusCode.OK; }) };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/") };

        using var _ = await http.GetAsync(new Uri("ermsystem/_apis/projects", UriKind.Relative));

        Assert.AreEqual("true", seen!.Headers.GetValues("X-VSS-ForceMsaPassThrough").Single());
        Assert.AreEqual("Suppress", seen.Headers.GetValues("X-TFS-FedAuthRedirect").Single());
        Assert.AreEqual("Bearer", seen.Headers.Authorization!.Scheme);
    }

    private sealed class CountingCredential : TokenCredential
    {
        public int Calls { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new($"token-{++Calls}", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class Responder(Func<HttpRequestMessage, HttpStatusCode> status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status(request)));
    }
}
