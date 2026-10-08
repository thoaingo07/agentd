using System.Text.Json.Serialization;
using Agentd.Bff.Endpoints;
using Agentd.Bff.Http;
using Agentd.Bff.Hubs;
using Agentd.Bff.OpenApi;
using Agentd.Bff.Security;
using Agentd.Bff.Setup;
using Agentd.Bff.Testing;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentd.Bff;

/// <summary>The screen-shaped API for the Vue app (<c>/api</c>). The Host calls <see cref="AddBff"/> and <see cref="MapBff"/>.</summary>
public static class BffModule
{
    public const string DocumentName = "v1";

    public static IServiceCollection AddBff(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddOpenApi(DocumentName, o => o.AddDocumentTransformer<BffOnlyDocumentTransformer>());
        // Numbers are numbers in the contract (the Web default also accepts them as strings, which types them `number | string`).
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, LocalUserAuthenticationHandler>(LocalUserAuthenticationHandler.SchemeName, _ => { })
            .AddScheme<AuthenticationSchemeOptions, CloudflareAccessAuthenticationHandler>(CloudflareAccessAuthenticationHandler.SchemeName, _ => { })
            .AddScheme<AuthenticationSchemeOptions, TailscaleAuthenticationHandler>(TailscaleAuthenticationHandler.SchemeName, _ => { })
            .AddSetupSession();   // the one-time setup link's cookie: /api/setup only
        // Agentd:Auth:Mode picks the scheme; endpoints only say RequireAuthorization().
        services.AddOptions<AuthenticationOptions>().Configure<IOptions<BffAuthOptions>>((o, auth) =>
            o.DefaultScheme = auth.Value.Mode switch
            {
                AuthMode.CloudflareAccess => CloudflareAccessAuthenticationHandler.SchemeName,
                AuthMode.Tailscale => TailscaleAuthenticationHandler.SchemeName,
                _ => LocalUserAuthenticationHandler.SchemeName,
            });
        services.AddOptions<TailscaleOptions>().BindConfiguration(BffAuthOptions.Section + ":Tailscale");
        services.AddOptions<CloudflareAccessOptions>().BindConfiguration(BffAuthOptions.Section + ":CloudflareAccess");
        services.AddHttpClient(CloudflareAccessKeys.HttpClientName);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CloudflareAccessKeys>();
        services.AddSingleton<Testing.CspReportLog>();
        services.AddAuthorizationBuilder().AddSetupPolicy();
        services.AddOptions<BffAuthOptions>().BindConfiguration(BffAuthOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BffAuthOptions>, BffAuthOptionsValidator>();
        services.AddAntiforgery();
        services.AddOptions<AntiforgeryOptions>().Configure<IConfiguration, IOptions<BffAuthOptions>>((o, configuration, auth) =>
        {
            o.HeaderName = AntiforgeryFilter.HeaderName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.Path = "/";
            o.SuppressXFrameOptionsHeader = true;                // CSP frame-ancestors (T3.6)

            // Served over https: "__Host-" + Secure. Behind Cloudflare the browser is always on https (the daemon sees it
            // through X-Forwarded-Proto). Plain http is only allowed on loopback (Mode None), where ASP.NET refuses to
            // issue a Secure antiforgery cookie, so the cookie is "agentd.af" without Secure.
            var https = auth.Value.Mode == AuthMode.CloudflareAccess || BffAuthOptionsValidator.AllHttps(configuration);
            o.Cookie.Name = https ? AntiforgeryFilter.SecureCookieName : AntiforgeryFilter.CookieName;
            o.Cookie.SecurePolicy = https ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
        services.AddSignalR();
        services.AddCspReportRateLimit();
        services.AddSingleton<EventStreams>();
        return services;
    }

    /// <summary>
    /// Behind Cloudflare Tunnel or <c>tailscale serve</c> only: trust <c>X-Forwarded-Proto</c> / <c>X-Forwarded-For</c> from
    /// the loopback proxy (cloudflared, tailscaled), so the daemon sees https and the visitor's address. Call it first. In Mode None the
    /// headers are left alone, and the local-user handler refuses forwarded requests.
    /// </summary>
    public static IApplicationBuilder UseBffForwardedHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.ApplicationServices.GetRequiredService<IOptions<BffAuthOptions>>().Value.Mode is not (AuthMode.CloudflareAccess or AuthMode.Tailscale))
        {
            return app;
        }

        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
        // The defaults already trust only loopback proxies; say so explicitly.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("127.0.0.0/8"));
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("::1/128"));
        return app.UseForwardedHeaders(options);
    }

    /// <summary>Maps <c>/api/*</c>, all requiring an authenticated user (the local user until Phase 5).</summary>
    public static RouteGroupBuilder MapBff(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            // The API shape is only published in Development (and to Admins later).
            endpoints.MapOpenApi("/openapi/{documentName}.json");
        }

        endpoints.MapTestSeeding(endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>());   // E2E environment only
        endpoints.MapCspReport();   // outside the groups: anonymous, no antiforgery (browsers send it)
        var api = endpoints.MapGroup("/api").RequireAuthorization().AddEndpointFilter<AntiforgeryFilter>();
        endpoints.MapGroup("/bff").RequireAuthorization().AddEndpointFilter<AntiforgeryFilter>().MapSession();
        endpoints.MapSetupSession().MapSetupSteps();   // GET /setup (the one-time link) and /api/setup (the setup session only)
        api.MapJobReads();
        api.MapJobActions();
        api.MapConfig();
        api.MapPermissions();
        api.MapIdeas();
        api.MapWorkItems();
        // Settings: the setup steps again, for Admins (antiforgery from the /api group), with Health instead of Finish.
        // Same shapes as /api/setup, so the contract (openapi.json) documents them once, there.
        api.MapGroup("/settings").RequireAuthorization(p => p.RequireRole("Admin")).MapSetupSteps("Settings", finish: false).ExcludeFromDescription();
        endpoints.MapHub<EventsHub>(EventsHub.Path).RequireAuthorization();
        return api;
    }
}
