using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Agentd.Bff.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentd.Bff.Security;

/// <summary><c>Agentd:Auth:Tailscale</c>: who may use the web UI through <c>tailscale serve</c>, and who is an Admin.</summary>
public sealed class TailscaleOptions
{
    /// <summary>Tailnet logins (e.g. <c>alice@example.com</c>) with the Admin role; everyone else is a User. At least one.</summary>
    public IReadOnlyList<string> AdminLogins { get; set; } = [];

    /// <summary>Optional: only these logins (and the admins) get in; empty = everyone in the tailnet.</summary>
    public IReadOnlyList<string> AllowedLogins { get; set; } = [];
}

/// <summary>
/// Signs in tailnet users who reach the web UI through <c>tailscale serve</c> on this machine (https on the machine's
/// tailnet name, nothing public). Serve adds <c>Tailscale-User-Login</c> for tailnet traffic and strips any copy a client
/// sends, so on a loopback connection it names the visitor; a process on this machine could set it too, but it is
/// already trusted as the local Admin. Without Serve's headers (an SSH tunnel) it's the local Admin as in Mode None;
/// a proxied request without an identity (a tagged device, Funnel, another proxy) isn't signed in.
/// </summary>
public sealed class TailscaleAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<TailscaleOptions> tailscale) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Tailscale";
    public const string LoginHeader = "Tailscale-User-Login";
    public const string NameHeader = "Tailscale-User-Name";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Serve connects from this machine; in-process test servers have no remote address (local too).
        var remote = Context.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var login = Request.Headers[LoginHeader].ToString().Trim();
        if (login.Length == 0)
        {
            return Task.FromResult(LocalUserAuthenticationHandler.IsForwarded(Request.Headers)
                ? AuthenticateResult.NoResult()   // proxied, but Serve didn't name anyone
                : AuthenticateResult.Success(Ticket("local", "local", "Admin")));
        }

        var settings = tailscale.CurrentValue;
        var admin = settings.AdminLogins.Contains(login, StringComparer.OrdinalIgnoreCase);
        if (!admin && settings.AllowedLogins.Count > 0 && !settings.AllowedLogins.Contains(login, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail($"{login} is not in Agentd:Auth:Tailscale:AllowedLogins."));
        }

        var name = Request.Headers[NameHeader].ToString().Trim();
        return Task.FromResult(AuthenticateResult.Success(Ticket(login, name.Length > 0 ? name : login, admin ? "Admin" : "User")));
    }

    private static AuthenticationTicket Ticket(string id, string name, string role) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role)], SchemeName)),
        SchemeName);
}
