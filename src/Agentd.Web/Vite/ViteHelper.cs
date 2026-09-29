using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Html;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentd.Web.Vite;

/// <summary>
/// Renders the tags a Razor view needs to boot one of the Vue apps in <c>ClientApps/</c>:
/// from Vite's build manifest (read once, cached) or, in Development with a dev server, from the
/// dev server via the Host proxy. Only external files are referenced, never inline code (CSP).
/// </summary>
public sealed class ViteHelper(IWebHostEnvironment environment, IOptions<ViteOptions> options)
{
    private readonly ViteOptions _options = options.Value;
    private readonly Lazy<IReadOnlyDictionary<string, ManifestChunk>> _manifest = new(() => LoadManifest(environment, options.Value));
    private readonly ConcurrentDictionary<string, ViteAssets> _cache = new(StringComparer.Ordinal);

    public bool UsesDevServer => environment.IsDevelopment() && _options.DevServerUrl is not null;

    /// <summary>Stylesheets, module preloads and entry script for <paramref name="app"/> (e.g. "dashboard").</summary>
    public ViteAssets Assets(string app)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        if (UsesDevServer)
        {
            return new ViteAssets([Url("@vite/client"), Url(ViteOptions.EntryOf(app))], [], []);
        }

        return _cache.GetOrAdd(app, ResolveFromManifest);
    }

    /// <summary>All tags for <paramref name="app"/>, for the layout's &lt;head&gt;.</summary>
    public IHtmlContent Tags(string app)
    {
        var assets = Assets(app);
        var html = new HtmlContentBuilder();
        foreach (var href in assets.Styles)
        {
            html.AppendHtmlLine($"""<link rel="stylesheet" href="{Encode(href)}" />""");
        }

        foreach (var href in assets.Preloads)
        {
            html.AppendHtmlLine($"""<link rel="modulepreload" href="{Encode(href)}" />""");
        }

        foreach (var src in assets.Scripts)
        {
            html.AppendHtmlLine($"""<script type="module" src="{Encode(src)}"></script>""");
        }

        return html;
    }

    /// <summary>URL of a file under this library's static assets (e.g. "theme-init.js").</summary>
    public string Url(string path) => _options.Base.TrimEnd('/') + "/" + path.TrimStart('/');

    private ViteAssets ResolveFromManifest(string app)
    {
        var manifest = _manifest.Value;
        var entryKey = ViteOptions.EntryOf(app);
        if (!manifest.TryGetValue(entryKey, out var entry))
        {
            throw new ViteManifestException($"App '{app}' ({entryKey}) is not in the Vite manifest.");
        }

        var styles = new List<string>();
        var preloads = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Walk(string key, ManifestChunk chunk, bool isEntry)
        {
            if (!visited.Add(key))
            {
                return;
            }

            foreach (var import in chunk.Imports)
            {
                if (manifest.TryGetValue(import, out var imported))
                {
                    Walk(import, imported, isEntry: false);
                }
            }

            styles.AddRange(chunk.Css.Select(Url));
            if (!isEntry)
            {
                preloads.Add(Url(chunk.File));
            }
        }

        Walk(entryKey, entry, isEntry: true);
        return new ViteAssets([Url(entry.File)], styles.Distinct().ToList(), preloads.Distinct().ToList());
    }

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);

    private static Dictionary<string, ManifestChunk> LoadManifest(IWebHostEnvironment environment, ViteOptions options)
    {
        var file = environment.WebRootFileProvider.GetFileInfo(options.ManifestPath);
        if (!file.Exists)
        {
            throw new ViteManifestException(
                $"Vite manifest '{options.ManifestPath}' not found. Build the client apps (cd src/Agentd.Web && npm run build) or run with Aspire in Development.");
        }

        using var stream = file.CreateReadStream();
        return JsonSerializer.Deserialize<Dictionary<string, ManifestChunk>>(stream)
            ?? throw new ViteManifestException("The Vite manifest is empty.");
    }
}

/// <summary>The external files an app needs.</summary>
public sealed record ViteAssets(IReadOnlyList<string> Scripts, IReadOnlyList<string> Styles, IReadOnlyList<string> Preloads);

/// <summary>One entry of Vite's manifest.</summary>
public sealed class ManifestChunk
{
    [JsonPropertyName("file")]
    public string File { get; init; } = "";

    [JsonPropertyName("css")]
    public IReadOnlyList<string> Css { get; init; } = [];

    [JsonPropertyName("imports")]
    public IReadOnlyList<string> Imports { get; init; } = [];
}

/// <summary>Thrown when the production build (manifest or an app entry) is missing.</summary>
public sealed class ViteManifestException(string message) : Exception(message);
