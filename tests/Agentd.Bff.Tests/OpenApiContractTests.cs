using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Agentd.Bff.Tests;

/// <summary>
/// The committed OpenAPI document (<c>ClientApps/shared/api/openapi.json</c>, the input of <c>npm run gen:api</c>)
/// must match the BFF. Set <c>AGENTD_UPDATE_OPENAPI=1</c> to rewrite it after changing an endpoint or view model.
/// </summary>
[TestClass]
public sealed class OpenApiContractTests
{
    private static readonly string s_committed = Path.Combine(RepoRoot(), "src", "Agentd.Web", "ClientApps", "shared", "api", "openapi.json");

    [TestMethod]
    public async Task The_committed_contract_matches_the_server()
    {
        var current = await DocumentAsync(Environments.Development);

        if (Environment.GetEnvironmentVariable("AGENTD_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(s_committed, current);
        }

        Assert.IsTrue(File.Exists(s_committed), $"{s_committed} is missing; run the tests with AGENTD_UPDATE_OPENAPI=1.");
        Assert.AreEqual(await File.ReadAllTextAsync(s_committed), current,
            "The BFF contract changed: run `AGENTD_UPDATE_OPENAPI=1 dotnet test --project tests/Agentd.Bff.Tests`, then `npm run gen:api` in src/Agentd.Web, and commit both.");
    }

    [TestMethod]
    public async Task The_document_has_only_browser_operations_with_stable_names()
    {
        var paths = JsonNode.Parse(await DocumentAsync(Environments.Development))!["paths"]!.AsObject();

        Assert.IsTrue(paths.All(p => p.Key.StartsWith("/api/", StringComparison.Ordinal) || p.Key.StartsWith("/bff/", StringComparison.Ordinal)));
        var operations = paths.SelectMany(p => p.Value!.AsObject().Select(o => o.Value!["operationId"]?.GetValue<string>())).ToList();
        CollectionAssert.IsSubsetOf(
            new[] { "GetDashboard", "GetJob", "GetJobEvents", "SearchHistory", "CancelJob", "RetryJob", "SendJobMessage", "RunWorkItem", "GetJobDiff" },
            operations);
        CollectionAssert.DoesNotContain(operations, null, "every operation is named");
    }

    [TestMethod]
    public async Task The_document_is_not_served_outside_development()
    {
        await using var app = await StartAsync(Environments.Production);

        using var response = await app.GetTestClient().GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<string> DocumentAsync(string environment)
    {
        await using var app = await StartAsync(environment);
        var json = await app.GetTestClient().GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        // Stable formatting, LF line endings, trailing newline: the file is diffed in git.
        return JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }

    private static async Task<WebApplication> StartAsync(string environment)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environment, ApplicationName = "Agentd.Host" });
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        var app = builder.Build();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Agentd.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Agentd.slnx not found above " + AppContext.BaseDirectory);
    }
}
