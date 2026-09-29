namespace Agentd.Bff.Vite;

/// <summary>Configuration section <c>Agentd:Web:Vite</c>.</summary>
public sealed class ViteOptions
{
    public const string Section = "Agentd:Web:Vite";

    /// <summary>Entry module, as it appears as a key in Vite's manifest (and as the dev-server path).</summary>
    public string Entry { get; set; } = "src/main.ts";

    /// <summary>Location of the manifest inside the web root (Vite's <c>build.manifest: true</c> default).</summary>
    public string ManifestPath { get; set; } = ".vite/manifest.json";

    /// <summary>
    /// Vite dev server URL (Development only). When set, the page loads modules from the dev server
    /// through the Host's proxy instead of reading the manifest. Aspire provides it via the
    /// <c>services:web:http:0</c> reference when this is empty.
    /// </summary>
    public Uri? DevServerUrl { get; set; }

    /// <summary>Paths forwarded to the Vite dev server in Development (modules, HMR, public files).</summary>
    public IList<string> DevServerPaths { get; } =
    [
        "/@vite", "/@id", "/@fs", "/src", "/node_modules", "/__vite_hmr", "/theme-init.js",
    ];
}
