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
          "_vendor-def456.js": {
            "file": "assets/vendor-def456.js",
            "css": ["assets/vendor-def456.css"]
          }
        }
        """;

    private const string TestManifestPath = "_content/Agentd.Web/test-manifest.json";

    private readonly string _webRoot = CreateWebRoot(withManifest);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseWebRoot(_webRoot);
        builder.UseSetting("ConnectionStrings:agentd", "Host=127.0.0.1;Port=1;Database=unused");
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
        if (withManifest)
        {
            File.WriteAllText(Path.Combine(content, "test-manifest.json"), Manifest);
        }

        return dir;
    }
}
