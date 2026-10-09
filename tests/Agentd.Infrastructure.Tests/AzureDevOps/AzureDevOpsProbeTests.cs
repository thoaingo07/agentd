using System.Net;
using System.Text;
using Agentd.Application.Jobs;
using Agentd.Application.Setup;
using Agentd.Infrastructure.AzureDevOps;

namespace Agentd.Infrastructure.Tests.AzureDevOps;

[TestClass]
public sealed class AzureDevOpsProbeTests : IDisposable
{
    private readonly FakeAdo _ado = new();

    [TestMethod]
    public async Task A_working_pat_runs_the_daemons_query_in_that_project()
    {
        _ado.On(HttpMethod.Post, "/myorg/Portal/_apis/wit/wiql", HttpStatusCode.OK, """{"workItems":[]}""");

        var check = await Probe().TestAsync(new AzureDevOpsConnection("myorg", "Portal", true, "pat-123"), new JobOptions(), CancellationToken.None);

        Assert.IsTrue(check.Ok, check.Message);
        Assert.Contains("0 work item(s) tagged 'ai-workflow'", check.Message);
        var request = _ado.Requests.Single();
        Assert.AreEqual("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":pat-123")), request.Auth);
        Assert.Contains("ai-workflow", request.Body!);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.NotFound)]
    public async Task A_rejected_token_or_unknown_project_fails_with_a_fix(HttpStatusCode status)
    {
        _ado.On(HttpMethod.Post, "/myorg/Portal/_apis/wit/wiql", status);

        var check = await Probe().TestAsync(new AzureDevOpsConnection("myorg", "Portal", true, "bad"), new JobOptions(), CancellationToken.None);

        Assert.IsFalse(check.Ok);
        Assert.Contains("Work Items (read & write)", check.Fix!);
        Assert.DoesNotContain("bad", check.Message.Replace("bad request", string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Az_login_uses_the_cli_credential()
    {
        _ado.On(HttpMethod.Post, "/myorg/Portal/_apis/wit/wiql", HttpStatusCode.OK, """{"workItems":[]}""");

        var check = await Probe().TestAsync(new AzureDevOpsConnection("myorg", "Portal", false, null), new JobOptions(), CancellationToken.None);

        Assert.IsTrue(check.Ok, check.Message);
        Assert.AreEqual("Bearer from-az", _ado.Requests.Single().Auth);
    }

    [TestMethod]
    public async Task A_service_principal_signs_in_with_its_own_credential_and_a_failure_names_what_to_check()
    {
        _ado.On(HttpMethod.Post, "/myorg/Portal/_apis/wit/wiql", HttpStatusCode.OK, """{"workItems":[]}""");
        ServicePrincipal? used = null;
        var probe = new AzureDevOpsProbe(() => _ado, new FixedAuth(), sp =>
        {
            used = sp;
            return new BearerAuth("from-app");
        });
        var app = new ServicePrincipal("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", "app-SECRET");

        var ok = await probe.TestAsync(new AzureDevOpsConnection("myorg", "Portal", false, null, app), new JobOptions(), CancellationToken.None);
        _ado.On(HttpMethod.Post, "/myorg/Portal/_apis/wit/wiql", HttpStatusCode.Unauthorized);
        var refused = await probe.TestAsync(new AzureDevOpsConnection("myorg", "Portal", false, null, app), new JobOptions(), CancellationToken.None);

        Assert.IsTrue(ok.Ok, ok.Message);
        Assert.AreEqual(("Bearer from-app", app), (_ado.Requests[0].Auth, used));
        Assert.IsFalse(refused.Ok);
        Assert.Contains("connected to this Entra tenant", refused.Fix!);
        Assert.DoesNotContain("SECRET", app.ToString(), "a logged record never prints the secret");
    }

    [TestMethod]
    public async Task A_service_principal_without_its_settings_fails_on_use_and_says_what_to_set()
    {
        var auth = AzCliAuthProvider.ForServicePrincipal("tenant", "  ", "secret");

        var error = await Assert.ThrowsExactlyAsync<AdoException>(async () => await auth.GetAsync(false, CancellationToken.None));

        Assert.Contains("ClientId", error.Message);
        Assert.IsInstanceOfType<AzCliAuthProvider>(AzCliAuthProvider.ForServicePrincipal("contoso.onmicrosoft.com", "22222222-2222-2222-2222-222222222222", "s"));
    }

    public void Dispose() => _ado.Dispose();

    private AzureDevOpsProbe Probe() => new(() => _ado, new FixedAuth());

    private sealed class BearerAuth(string token) : IAdoAuthProvider
    {
        public ValueTask<System.Net.Http.Headers.AuthenticationHeaderValue> GetAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token));
    }

    private sealed class FixedAuth : IAdoAuthProvider
    {
        public ValueTask<System.Net.Http.Headers.AuthenticationHeaderValue> GetAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "from-az"));
    }
}
