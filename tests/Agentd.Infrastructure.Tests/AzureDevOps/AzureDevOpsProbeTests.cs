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

    public void Dispose() => _ado.Dispose();

    private AzureDevOpsProbe Probe() => new(() => _ado, new FixedAuth());

    private sealed class FixedAuth : IAdoAuthProvider
    {
        public ValueTask<System.Net.Http.Headers.AuthenticationHeaderValue> GetAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "from-az"));
    }
}
