using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentd.Bff.Http;

/// <summary>
/// Until SSO (Phase 5): every request from the loopback interface is the pseudo-user <c>local</c> with
/// the Admin role, so endpoints already require authorization and Phase 5 policies drop in unchanged.
/// Requests from any other address are not authenticated (401), and so are requests that came through a
/// proxy or tunnel on this machine (it connects from loopback, which would make every visitor the local
/// Admin): any forwarding header means "not local".
/// </summary>
public sealed class LocalUserAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalUser";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // In-process test servers have no remote address; that is local too.
        var remote = Context.Connection.RemoteIpAddress;
        if ((remote is not null && !IPAddress.IsLoopback(remote)) || IsForwarded(Request.Headers))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "local"), new Claim(ClaimTypes.Name, "local"), new Claim(ClaimTypes.Role, "Admin")],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    /// <summary>Headers set by reverse proxies and tunnels (nginx, Caddy, cloudflared, …).</summary>
    internal static bool IsForwarded(IHeaderDictionary headers) =>
        s_forwardingHeaders.Any(headers.ContainsKey);

    private static readonly string[] s_forwardingHeaders = ["Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Real-IP", "Cf-Connecting-IP", "Cf-Ray"];
}
