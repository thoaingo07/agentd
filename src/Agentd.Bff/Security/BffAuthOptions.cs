using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Agentd.Bff.Security;

/// <summary>How browser users sign in (<c>Agentd:Auth</c>).</summary>
public enum AuthMode
{
    /// <summary>No sign-in: the loopback user is the local Admin. Only allowed on a loopback-only binding.</summary>
    None,

    /// <summary>Cloudflare Tunnel + Access (T3.14).</summary>
    CloudflareAccess,

    /// <summary>OpenID Connect (Phase 5).</summary>
    Sso,
}

public sealed class BffAuthOptions
{
    public const string Section = "Agentd:Auth";

    public AuthMode Mode { get; set; } = AuthMode.None;
}

/// <summary>
/// Fails startup (fail closed) when Mode None would be reachable from other machines: any configured
/// URL that isn't loopback (<c>urls</c> / <c>Kestrel:Endpoints</c>), or <c>http_ports</c> / <c>https_ports</c>,
/// which bind every interface. Modes that aren't built yet are refused too.
/// </summary>
public sealed class BffAuthOptionsValidator(IConfiguration configuration) : IValidateOptions<BffAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, BffAuthOptions options)
    {
        if (options.Mode != AuthMode.None)
        {
            return ValidateOptionsResult.Fail($"Agentd:Auth:Mode '{options.Mode}' is not available yet; use None (loopback only).");
        }

        if (!string.IsNullOrWhiteSpace(configuration["http_ports"]) || !string.IsNullOrWhiteSpace(configuration["https_ports"]))
        {
            return ValidateOptionsResult.Fail("Agentd:Auth:Mode None can't use http_ports/https_ports (they bind every interface); set loopback urls instead.");
        }

        var urls = (configuration["urls"] ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(e => e["Url"]).OfType<string>());
        var exposed = urls.Where(u => Parse(u) is { } uri && !IsLoopback(uri)).ToList();
        return exposed.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Agentd:Auth:Mode None only listens on loopback, but these URLs aren't: {string.Join(", ", exposed)}. Bind 127.0.0.1, or reach the UI through an SSH tunnel.");
    }

    /// <summary>
    /// The URL, with "http://*:80" / "http://+:80" (every interface) as a non-loopback host. Null if it
    /// isn't a URL at all: Kestrel refuses to bind it, so nothing is exposed (and its own validation reports it).
    /// </summary>
    internal static Uri? Parse(string url) =>
        Uri.TryCreate(url.Replace("://*", "://wildcard.invalid", StringComparison.Ordinal).Replace("://+", "://wildcard.invalid", StringComparison.Ordinal), UriKind.Absolute, out var uri)
            ? uri
            : null;

    internal static bool IsLoopback(Uri uri) =>
        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>True when every configured URL is https (then the antiforgery cookie can be <c>__Host-</c> + Secure).</summary>
    internal static bool AllHttps(IConfiguration configuration)
    {
        var urls = (configuration["urls"] ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(e => e["Url"]).OfType<string>())
            .ToList();
        return urls.Count > 0 && urls.All(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }
}
