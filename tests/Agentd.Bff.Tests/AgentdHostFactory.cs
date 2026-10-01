using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentd.Bff.Tests;

/// <summary>Hosts agentd in-memory (no database contacted) for BFF/host-level tests.</summary>
internal sealed class AgentdHostFactory(IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    // The daemon creates its config home on startup: keep it out of the developer's ~/.agentd.
    private static readonly string s_home = Directory.CreateTempSubdirectory("agentd-test-home-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("AGENTD_HOME", s_home);
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:agentd", "Host=127.0.0.1;Port=1;Database=unused");
        builder.UseSetting("Agentd:Scheduler:Enabled", "false");   // no polling, scheduling or recovery in UI tests
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
    }
}
