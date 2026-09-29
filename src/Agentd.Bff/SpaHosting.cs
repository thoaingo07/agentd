using Agentd.Bff.Vite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentd.Bff;

/// <summary>
/// Hosts the Vue SPA through a Razor view. Production: assets from Vite's manifest in wwwroot.
/// Development: modules and HMR proxied to the Vite dev server (same origin as the Host).
/// </summary>
public static class SpaHosting
{
    /// <summary>Server-owned path prefixes that must never be answered by the SPA shell.</summary>
    private static readonly string[] s_reservedPrefixes = ["/api", "/bff", "/hubs", "/mcp"];

    public static IServiceCollection AddSpaHosting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllersWithViews().AddApplicationPart(typeof(SpaHosting).Assembly);
        services.AddOptions<ViteOptions>()
            .Bind(configuration.GetSection(ViteOptions.Section))
            .PostConfigure(o =>
            {
                // Aspire: host.WithReference(web) → configuration "services:web:http:0".
                if (o.DevServerUrl is null && Uri.TryCreate(configuration["services:web:http:0"], UriKind.Absolute, out var url))
                {
                    o.DevServerUrl = url;
                }
            });
        services.AddSingleton<ViteManifest>();
        services.AddSingleton<ViteAssetResolver>();
        return services;
    }

    public static WebApplication UseSpaHosting(this WebApplication app)
    {
        var vite = app.Services.GetRequiredService<IOptions<ViteOptions>>().Value;

        if (app.Environment.IsDevelopment() && vite.DevServerUrl is { } devServer)
        {
            // Vite modules, public files and the HMR websocket go to the dev server; the page stays on this origin.
            app.MapWhen(
                ctx => vite.DevServerPaths.Any(p => ctx.Request.Path.StartsWithSegments(p, StringComparison.Ordinal)),
                branch => branch.UseSpa(spa => spa.UseProxyToSpaDevelopmentServer(devServer)));
        }

        app.UseStaticFiles();
        app.UseRouting();

        foreach (var prefix in s_reservedPrefixes)
        {
            // Lower-precedence catch-alls: real endpoints under these prefixes (later phases) win.
            app.Map($"{prefix}/{{**rest}}", () => Results.NotFound()).WithOrder(int.MaxValue - 1);
            app.Map(prefix, () => Results.NotFound()).WithOrder(int.MaxValue - 1);
        }

        app.MapControllerRoute("spa-root", "/", new { controller = "Spa", action = "Index" });
        app.MapFallbackToController("Index", "Spa");
        return app;
    }
}
