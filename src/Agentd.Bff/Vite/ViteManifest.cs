using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace Agentd.Bff.Vite;

/// <summary>One entry of Vite's <c>.vite/manifest.json</c>.</summary>
public sealed class ManifestChunk
{
    [JsonPropertyName("file")]
    public string File { get; init; } = "";

    [JsonPropertyName("css")]
    public IReadOnlyList<string> Css { get; init; } = [];

    [JsonPropertyName("imports")]
    public IReadOnlyList<string> Imports { get; init; } = [];

    [JsonPropertyName("isEntry")]
    public bool IsEntry { get; init; }
}

/// <summary>Thrown when the production build (manifest or entry) is missing.</summary>
public sealed class ViteManifestException(string message) : Exception(message);

/// <summary>Reads Vite's build manifest from the web root once and resolves the assets for an entry.</summary>
public sealed class ViteManifest(IWebHostEnvironment environment, IOptions<ViteOptions> options)
{
    private readonly Lazy<IReadOnlyDictionary<string, ManifestChunk>> _chunks = new(() => Load(environment, options.Value));

    /// <summary>Assets for <see cref="ViteOptions.Entry"/>: stylesheets, preloads and the entry script.</summary>
    public ViteAssets ResolveEntry()
    {
        var entryKey = options.Value.Entry;
        var chunks = _chunks.Value;
        if (!chunks.TryGetValue(entryKey, out var entry))
        {
            throw new ViteManifestException($"Entry '{entryKey}' not found in the Vite manifest.");
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
                if (chunks.TryGetValue(import, out var imported))
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
        return new ViteAssets(Scripts: [Url(entry.File)], Styles: styles.Distinct().ToList(), Preloads: preloads.Distinct().ToList());
    }

    private static string Url(string file) => "/" + file.TrimStart('/');

    private static Dictionary<string, ManifestChunk> Load(IWebHostEnvironment environment, ViteOptions options)
    {
        var file = environment.WebRootFileProvider.GetFileInfo(options.ManifestPath);
        if (!file.Exists)
        {
            throw new ViteManifestException(
                $"Vite manifest '{options.ManifestPath}' not found in the web root. Build the web app (npm run build) or run with Aspire in Development.");
        }

        using var stream = file.CreateReadStream();
        return JsonSerializer.Deserialize<Dictionary<string, ManifestChunk>>(stream)
            ?? throw new ViteManifestException("The Vite manifest is empty.");
    }
}
