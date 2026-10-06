using System.Text.Json;
using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class ResourceEndpointTests
{
    [TestMethod]
    public async Task Resources_carry_the_machine_and_each_running_jobs_sample()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton<IQueryHandler<GetResources, ResourcesView>>(new Fixed());
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var body = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/resources", UriKind.Relative))).RootElement;

        CollectionAssert.AreEqual(new[] { "cpuPercent", "memoryTotal", "memoryAvailable", "diskTotal", "diskFree", "lowMemory", "lowDisk", "at" },
            body.GetProperty("machine").EnumerateObject().Select(p => p.Name).ToArray(), "the TS contract");
        var job = body.GetProperty("jobs")[0];
        Assert.AreEqual((7L, 180d, 2147483648L), (job.GetProperty("jobId").GetInt64(), job.GetProperty("cpuPercent").GetDouble(), job.GetProperty("memoryBytes").GetInt64()));
        Assert.AreEqual(JsonValueKind.Null, job.GetProperty("worktreeBytes").ValueKind, "not measured yet");
        Assert.IsTrue(body.GetProperty("machine").GetProperty("lowDisk").GetBoolean());
    }

    private sealed class Fixed : IQueryHandler<GetResources, ResourcesView>
    {
        public Task<ResourcesView> Handle(GetResources query, CancellationToken cancellationToken) => Task.FromResult(new ResourcesView(
            new MachineResources(35, 16L << 30, 6L << 30, 500L << 30, 3L << 30, DateTimeOffset.UnixEpoch),
            new Dictionary<long, JobResources> { [7] = new(180, 2L << 30, null, DateTimeOffset.UnixEpoch) }));
    }
}
