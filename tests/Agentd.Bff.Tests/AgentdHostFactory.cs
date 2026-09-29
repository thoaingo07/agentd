using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentd.Bff.Tests;

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
          "src/main.ts": {
            "file": "assets/main-abc123.js",
            "src": "src/main.ts",
            "isEntry": true,
            "css": ["assets/main-abc123.css"],
            "imports": ["_vendor-def456.js"]
          },
          "_vendor-def456.js": {
            "file": "assets/vendor-def456.js",
            "css": ["assets/vendor-def456.css"]
          }
        }
        """;

    private readonly string _webRoot = CreateWebRoot(withManifest);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
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

    private static string CreateWebRoot(bool withManifest)
    {
        var dir = Directory.CreateTempSubdirectory("agentd-wwwroot-").FullName;
        File.WriteAllText(Path.Combine(dir, "theme-init.js"), "/* theme */");
        if (withManifest)
        {
            Directory.CreateDirectory(Path.Combine(dir, ".vite"));
            File.WriteAllText(Path.Combine(dir, ".vite", "manifest.json"), Manifest);
        }

        return dir;
    }
}
