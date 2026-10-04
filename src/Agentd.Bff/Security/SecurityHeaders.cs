using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Agentd.Bff.Security;

public sealed class SecurityHeadersOptions
{
    /// <summary>The Vite dev server is proxied (Development + HMR): its client injects &lt;style&gt; elements, so styles need 'unsafe-inline'. Never in production.</summary>
    public bool AllowInlineStylesForDevServer { get; set; }

    /// <summary>Path prefix of Vite's content-hashed build output, cached forever.</summary>
    public string ImmutableAssetsPath { get; set; } = "/_content/Agentd.Web/assets";
}

/// <summary>
/// Strict CSP and the other security headers on <b>every</b> response (pages, assets, API, errors),
/// plus caching rules: <c>/api</c> and <c>/bff</c> never cached, hashed assets immutable, HTML revalidated.
/// See docs/security §3–4.
/// </summary>
public static class SecurityHeaders
{
    public const string ReportPath = "/api/csp-report";

    /// <summary>The enforced policy. <c>upgrade-insecure-requests</c> only over https (plain http is loopback only).</summary>
    public static string Policy(bool https, bool inlineStyles) =>
        string.Join("; ",
            new[]
            {
                "default-src 'none'",
                "script-src 'self'",
                inlineStyles ? "style-src 'self' 'unsafe-inline'" : "style-src 'self'",
                "img-src 'self' data:",
                "font-src 'self'",
                "connect-src 'self'",
                "manifest-src 'self'",
                "base-uri 'none'",
                "form-action 'self'",
                "frame-ancestors 'none'",
                "object-src 'none'",
                https ? "upgrade-insecure-requests" : null,
                "report-uri " + ReportPath,
                "report-to csp",
            }.OfType<string>());

    /// <summary>Trusted Types are reported, not enforced, until Vue, base-ui-vue and SignalR are confirmed clean (Phase 10).</summary>
    public const string ReportOnlyPolicy = "require-trusted-types-for 'script'; trusted-types vue; report-uri " + ReportPath + "; report-to csp";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, Action<SecurityHeadersOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = new SecurityHeadersOptions();
        configure?.Invoke(options);
        return app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                Apply(context, options);
                return Task.CompletedTask;
            });
            return next(context);
        });
    }

    private static void Apply(HttpContext context, SecurityHeadersOptions options)
    {
        var request = context.Request;
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = Policy(request.IsHttps, options.AllowInlineStylesForDevServer);
        headers.ContentSecurityPolicyReportOnly = ReportOnlyPolicy;
        headers["Reporting-Endpoints"] = $"csp=\"{ReportPath}\"";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        if (request.IsHttps)
        {
            headers.StrictTransportSecurity = "max-age=31536000";
        }

        var path = request.Path;
        if (path.StartsWithSegments("/api", StringComparison.Ordinal) || path.StartsWithSegments("/bff", StringComparison.Ordinal))
        {
            headers.CacheControl = "no-store";
        }
        else if (path.StartsWithSegments(options.ImmutableAssetsPath, StringComparison.Ordinal) && context.Response.StatusCode == StatusCodes.Status200OK)
        {
            headers.CacheControl = "public, max-age=31536000, immutable";
        }
        else if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            headers.CacheControl = "no-cache";
        }
    }
}
