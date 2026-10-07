using Agentd.Web.Vite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentd.Web;

/// <summary>
/// Hosts the Vue apps through Razor views. Production: assets from Vite's manifest (this library's
/// static web assets). Development: everything under the Vite base is proxied to the Vite dev server,
/// including HMR, so the browser only talks to the Host's origin.
/// </summary>
public static class WebHosting
{
    /// <summary>Server-owned path prefixes that must never be answered by an SPA shell.</summary>
    private static readonly string[] s_reservedPrefixes = ["/api", "/bff", "/hubs", "/mcp"];

    public static IServiceCollection AddWebHosting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllersWithViews().AddApplicationPart(typeof(WebHosting).Assembly);
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
        services.AddSingleton<ViteHelper>();
        return services;
    }

    /// <param name="app">The application.</param>
    /// <param name="afterRouting">Middleware that needs the matched endpoint (authentication, authorization).</param>
    public static WebApplication UseWebHosting(this WebApplication app, Action<IApplicationBuilder>? afterRouting = null)
    {
        var vite = app.Services.GetRequiredService<IOptions<ViteOptions>>().Value;

        if (app.Environment.IsDevelopment() && vite.DevServerUrl is { } devServer)
        {
            var basePath = new PathString(vite.Base.TrimEnd('/'));
            app.MapWhen(
                ctx => ctx.Request.Path.StartsWithSegments(basePath, StringComparison.Ordinal),
                branch => branch.UseSpa(spa => spa.UseProxyToSpaDevelopmentServer(devServer)));
        }

        // The files on disk first (development, dotnet run); the copy embedded in this assembly after (single-file agentd).
        var embedded = new EmbeddedWebAssets(typeof(WebHosting).Assembly);
        if (embedded.HasAssets)
        {
            app.Environment.WebRootFileProvider = new CompositeFileProvider(app.Environment.WebRootFileProvider, embedded);
        }

        app.UseStaticFiles();
        app.UseRouting();
        afterRouting?.Invoke(app);

        foreach (var prefix in s_reservedPrefixes)
        {
            // Lower-precedence catch-alls: real endpoints under these prefixes (later phases) win.
            app.Map($"{prefix}/{{**rest}}", () => Results.NotFound()).WithOrder(int.MaxValue - 1);
            app.Map(prefix, () => Results.NotFound()).WithOrder(int.MaxValue - 1);
        }

        app.MapControllerRoute("dashboard", "/", new { controller = "Spa", action = "Dashboard" });
        app.MapControllerRoute("setup", "setup/wizard/{**rest}", new { controller = "Spa", action = "Setup" });
        app.MapFallbackToController("Dashboard", "Spa");
        return app;
    }
}
