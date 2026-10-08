using System.Net;
using Microsoft.AspNetCore.Http;

namespace Agentd.Bff.Security;

/// <summary>
/// What actually connected to the daemon (e.g. <c>tailscale serve</c> on loopback), recorded before forwarded headers
/// replace <see cref="ConnectionInfo.RemoteIpAddress"/> with the visitor's address and remove <c>X-Forwarded-For</c>.
/// </summary>
/// <param name="Address">The connecting peer's address.</param>
/// <param name="Forwarded">Whether the request came through a proxy or tunnel (it had forwarding headers).</param>
public sealed record ProxyPeer(IPAddress? Address, bool Forwarded)
{
    /// <summary>The peer as recorded; without forwarded-headers handling, the connection as it is.</summary>
    public static ProxyPeer Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<ProxyPeer>()
            ?? new ProxyPeer(context.Connection.RemoteIpAddress, Http.LocalUserAuthenticationHandler.IsForwarded(context.Request.Headers));
    }
}
