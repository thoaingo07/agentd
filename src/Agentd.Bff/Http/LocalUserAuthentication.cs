using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentd.Bff.Http;

/// <summary>
/// Until SSO (Phase 5): every request from the loopback interface is the pseudo-user <c>local</c> with
/// the Admin role, so endpoints already require authorization and Phase 5 policies drop in unchanged.
/// Requests from any other address are not authenticated (401).
/// </summary>
public sealed class LocalUserAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalUser";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // In-process test servers have no remote address; that is local too.
        var remote = Context.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "local"), new Claim(ClaimTypes.Name, "local"), new Claim(ClaimTypes.Role, "Admin")],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
