namespace Agentd.Web.Vite;

/// <summary>Configuration section <c>Agentd:Web:Vite</c>.</summary>
public sealed class ViteOptions
{
    public const string Section = "Agentd:Web:Vite";

    /// <summary>URL prefix of this library's static web assets; must match <c>base</c> in vite.config.ts.</summary>
    public string Base { get; set; } = "/_content/Agentd.Web/";

    /// <summary>Manifest path inside the web root (<c>build.manifest</c> in vite.config.ts).</summary>
    public string ManifestPath { get; set; } = "_content/Agentd.Web/manifest.json";

    /// <summary>
    /// Vite dev server URL (Development only). When set, apps load from the dev server through the
    /// Host proxy instead of the manifest. Aspire provides it via <c>services:web:http:0</c> when empty.
    /// </summary>
    public Uri? DevServerUrl { get; set; }

    /// <summary>Manifest key / dev-server path of an app's entry module.</summary>
    public static string EntryOf(string app) => $"ClientApps/{app}/main.ts";
}
