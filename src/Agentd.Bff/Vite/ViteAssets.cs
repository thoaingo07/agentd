using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentd.Bff.Vite;

/// <summary>The external files the SPA shell must reference. Never inline code (CSP).</summary>
public sealed record ViteAssets(IReadOnlyList<string> Scripts, IReadOnlyList<string> Styles, IReadOnlyList<string> Preloads);

/// <summary>Chooses dev-server modules (Development + dev server configured) or the built manifest assets.</summary>
public sealed class ViteAssetResolver(IHostEnvironment environment, IOptions<ViteOptions> options, ViteManifest manifest)
{
    public bool UsesDevServer => environment.IsDevelopment() && options.Value.DevServerUrl is not null;

    public ViteAssets Resolve() => UsesDevServer
        ? new ViteAssets(Scripts: ["/@vite/client", "/" + options.Value.Entry.TrimStart('/')], Styles: [], Preloads: [])
        : manifest.ResolveEntry();
}
