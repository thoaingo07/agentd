using Agentd.Infrastructure.AzureDevOps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Tests.AzureDevOps;

/// <summary>
/// Read-only checks against a real Azure DevOps org using the machine's `az login`.
/// Opt-in: set AGENTD_LIVE_ADO="org/project" (e.g. ermsystem/Portal). The PAT is read from the Host's
/// user-secrets (Agentd:AzureDevOps:Pat, id "agentd-host") or AGENTD_LIVE_ADO_PAT; without one it uses `az login`.
/// Never writes anything.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class LiveReadOnlyTests
{
    [TestMethod]
    public async Task Query_and_read_a_work_item_with_az_login()
    {
        var target = Environment.GetEnvironmentVariable("AGENTD_LIVE_ADO");
        if (string.IsNullOrWhiteSpace(target) || target.Split('/') is not [var org, var project])
        {
            Assert.Inconclusive("Set AGENTD_LIVE_ADO=org/project to run live read-only Azure DevOps checks.");
            return;
        }

        var pat = Environment.GetEnvironmentVariable("AGENTD_LIVE_ADO_PAT")
            ?? new ConfigurationBuilder().AddUserSecrets("agentd-host").Build()["Agentd:AzureDevOps:Pat"];
        using var azCli = new AzCliAuthProvider();
        IAdoAuthProvider auth = string.IsNullOrWhiteSpace(pat)
            ? azCli
            : new PatAuthProvider(Options.Create(new AzureDevOpsOptions { Pat = pat }));
        using var handler = new AdoAuthHandler(auth) { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false } };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/") };
        var source = new AzureDevOpsWorkItemSource(http, Options.Create(new AzureDevOpsOptions { Organization = org, Project = project }));

        // The same tagged query agentd polls with (may legitimately return nothing): proves auth + WIQL.
        var refs = await source.QueryTaggedAsync("ai-workflow", "ai-in-progress", ["New", "Active", "To Do", "Doing"], default);
        Assert.IsNotNull(refs);

        // Optionally read one known item: AGENTD_LIVE_ADO_ITEM=<id>.
        if (int.TryParse(Environment.GetEnvironmentVariable("AGENTD_LIVE_ADO_ITEM"), out var id))
        {
            var item = await source.GetAsync(id, default);
            Assert.IsNotNull(item);
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.Title));
        }
    }
}
