using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentd.Web.Tests;

/// <summary>
/// Hosts agentd in-memory with a temporary web root. By default it contains a Vite production build
/// (manifest + assets). No database is contacted: /healthz is not exercised here.
/// </summary>
internal sealed class AgentdHostFactory(
    IReadOnlyDictionary<string, string?>? settings = null,
    string environment = "Testing",
    bool withManifest = true) : WebApplicationFactory<Program>
{
    public const string Manifest = """
        {
          "ClientApps/dashboard/main.ts": {
            "file": "assets/dashboard-abc123.js",
            "src": "ClientApps/dashboard/main.ts",
            "isEntry": true,
            "css": ["assets/dashboard-abc123.css"],
            "imports": ["_vendor-def456.js"]
          },
          "ClientApps/setup/main.ts": {
            "file": "assets/setup-0f1e2d.js",
            "src": "ClientApps/setup/main.ts",
            "isEntry": true,
            "imports": ["_vendor-def456.js"]
          },
          "_vendor-def456.js": {
            "file": "assets/vendor-def456.js",
            "css": ["assets/vendor-def456.css"]
          }
        }
        """;

    private const string TestManifestPath = "_content/Agentd.Web/test-manifest.json";

    private readonly string _webRoot = CreateWebRoot(withManifest);

    // The daemon creates its config home on startup: keep it out of the developer's ~/.agentd.
    private static readonly string s_home = Directory.CreateTempSubdirectory("agentd-test-home-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("AGENTD_HOME", s_home);
        builder.UseEnvironment(environment);
        builder.UseWebRoot(_webRoot);
        builder.UseSetting("ConnectionStrings:agentd", "Host=127.0.0.1;Port=1;Database=unused");
        builder.UseSetting("Agentd:Scheduler:Enabled", "false");   // no polling, scheduling or recovery in UI tests
        builder.UseSetting("Agentd:Messaging:Providers:Discord:Enabled", "false");   // no chat (and no bot token) in UI tests
        // A test-only file name, so a locally built Agentd.Web/wwwroot (static web assets) never interferes.
        builder.UseSetting("Agentd:Web:Vite:ManifestPath", TestManifestPath);
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

    private static string CreateWebRoot(bool withManifest)
    {
        var dir = Directory.CreateTempSubdirectory("agentd-wwwroot-").FullName;
        // Mirrors Agentd.Web's static web assets under /_content/Agentd.Web/.
        var content = Path.Combine(dir, "_content", "Agentd.Web");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "theme-init.js"), "/* theme */");
        Directory.CreateDirectory(Path.Combine(content, "assets"));
        File.WriteAllText(Path.Combine(content, "assets", "dashboard-abc123.js"), "/* app */");
        if (withManifest)
        {
            File.WriteAllText(Path.Combine(content, "test-manifest.json"), Manifest);
        }

        return dir;
    }
}
