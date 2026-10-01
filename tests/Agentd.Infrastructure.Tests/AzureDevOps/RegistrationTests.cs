using System.Net;
using System.Net.Http.Headers;
using Agentd.Application.Ports;
using Agentd.Infrastructure.AzureDevOps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Agentd.Infrastructure.Tests.AzureDevOps;

[TestClass]
public sealed class RegistrationTests
{
    [TestMethod]
    public async Task Each_request_carries_the_ado_headers_exactly_once()
    {
        var capture = new Capture();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentd:AzureDevOps:Organization"] = "org",
            ["Agentd:AzureDevOps:Project"] = "proj",
        }).Build();
        var services = new ServiceCollection().AddLogging().AddAzureDevOps(config);
        services.AddSingleton<IAdoAuthProvider>(new StaticAuth());
        services.Configure<HttpClientFactoryOptions>(DependencyInjection.HttpClientName, o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = capture));
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IWorkItemSource>().QueryTaggedAsync("ai-workflow", "ai-in-progress", ["New"], default);
        await provider.GetRequiredService<IPullRequestService>().FindOpenAsync(
            new Domain.Repositories.Repository(Domain.Jobs.ValueObjects.RepositoryName.From("r"), "git@ssh.dev.azure.com:v3/org/proj/r", new Domain.Repositories.AzureDevOpsRepo("org", "proj", "r"), "main", null, []),
            Domain.Jobs.ValueObjects.BranchName.For(Domain.Jobs.ValueObjects.WorkItemId.From(1), "x"),
            default);

        Assert.HasCount(2, capture.Requests);
        foreach (var headers in capture.Requests)
        {
            CollectionAssert.AreEqual(new[] { "true" }, headers.GetValues("X-VSS-ForceMsaPassThrough").ToArray());
            CollectionAssert.AreEqual(new[] { "Suppress" }, headers.GetValues("X-TFS-FedAuthRedirect").ToArray());
        }
    }

    private sealed class StaticAuth : IAdoAuthProvider
    {
        public ValueTask<AuthenticationHeaderValue> GetAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AuthenticationHeaderValue("Bearer", "t"));
    }

    private sealed class Capture : HttpMessageHandler
    {
        public List<HttpRequestHeaders> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Headers);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"workItems":[],"value":[],"count":0}""", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
