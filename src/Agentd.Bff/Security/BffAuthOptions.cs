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

    /// <summary><c>tailscale serve</c> on this machine: tailnet users by their Tailscale login.</summary>
    Tailscale,

    /// <summary>OpenID Connect (Phase 5).</summary>
    Sso,
}

public sealed class BffAuthOptions
{
    public const string Section = "Agentd:Auth";

    public AuthMode Mode { get; set; } = AuthMode.None;
}

/// <summary>
/// Fails startup (fail closed) when the daemon would be reachable from other machines (both modes listen on
/// loopback only; with CloudflareAccess, cloudflared on the same machine is the only way in): any configured
/// URL that isn't loopback (<c>urls</c> / <c>Kestrel:Endpoints</c>), or <c>http_ports</c> / <c>https_ports</c>,
/// which bind every interface. Modes that aren't built yet are refused too.
/// </summary>
public sealed class BffAuthOptionsValidator(IConfiguration configuration) : IValidateOptions<BffAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, BffAuthOptions options)
    {
        if (options.Mode == AuthMode.Sso)
        {
            return ValidateOptionsResult.Fail("Agentd:Auth:Mode 'Sso' is not available yet (Phase 5); use None or CloudflareAccess.");
        }

        if (options.Mode == AuthMode.Tailscale && !configuration.GetSection(BffAuthOptions.Section + ":Tailscale:AdminLogins").GetChildren().Any(c => !string.IsNullOrWhiteSpace(c.Value)))
        {
            return ValidateOptionsResult.Fail("Agentd:Auth:Mode Tailscale needs Agentd:Auth:Tailscale:AdminLogins (your tailnet login, e.g. alice@example.com).");
        }

        if (options.Mode == AuthMode.CloudflareAccess)
        {
            var access = configuration.GetSection(BffAuthOptions.Section + ":CloudflareAccess");
            if (string.IsNullOrWhiteSpace(access["TeamDomain"]) || string.IsNullOrWhiteSpace(access["Audience"]))
            {
                return ValidateOptionsResult.Fail("Agentd:Auth:Mode CloudflareAccess needs Agentd:Auth:CloudflareAccess:TeamDomain and :Audience (the Access application's AUD tag).");
            }
        }

        if (!string.IsNullOrWhiteSpace(configuration["http_ports"]) || !string.IsNullOrWhiteSpace(configuration["https_ports"]))
        {
            return ValidateOptionsResult.Fail("agentd can't use http_ports/https_ports (they bind every interface); set loopback urls instead.");
        }

        var urls = (configuration["urls"] ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(e => e["Url"]).OfType<string>());
        var exposed = urls.Where(u => Parse(u) is { } uri && !IsLoopback(uri)).ToList();
        return exposed.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"agentd only listens on loopback (Mode {options.Mode}), but these URLs aren't: {string.Join(", ", exposed)}. Bind 127.0.0.1, or reach the UI through an SSH tunnel.");
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
