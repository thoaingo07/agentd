using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentd.Bff.Tests;

/// <summary>
/// Hosts agentd in-memory with a temporary web root holding a stub index.html.
/// No database is contacted: /healthz is not exercised here (see Infrastructure.Tests).
/// </summary>
internal sealed class AgentdHostFactory(IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    private readonly string _webRoot = CreateWebRoot();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseWebRoot(_webRoot);
        builder.UseSetting("ConnectionStrings:agentd", "Host=127.0.0.1;Port=1;Database=unused");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_webRoot))
        {
            Directory.Delete(_webRoot, recursive: true);
        }
    }

    private static string CreateWebRoot()
    {
        var dir = Directory.CreateTempSubdirectory("agentd-wwwroot-").FullName;
        File.WriteAllText(Path.Combine(dir, "index.html"), "<!doctype html><title>agentd</title><div id=\"app\"></div>");
        return dir;
    }
}
