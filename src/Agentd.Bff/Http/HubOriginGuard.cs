using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Http;

/// <summary>
/// WebSocket upgrades carry no antiforgery token, so <c>/hubs/*</c> (negotiate and connect) only
/// accepts the app's own origin: the request's scheme + host, or <c>Agentd:Web:PublicOrigin</c> when a proxy
/// rewrites the host. A missing or foreign <c>Origin</c> → 403.
/// </summary>
public static class HubOriginGuard
{
    public static IApplicationBuilder UseBffHubOriginGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var publicOrigin = app.ApplicationServices.GetRequiredService<IConfiguration>()["Agentd:Web:PublicOrigin"]?.TrimEnd('/');
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/hubs", StringComparison.Ordinal) && !IsAllowed(context.Request, publicOrigin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    internal static bool IsAllowed(HttpRequest request, string? publicOrigin)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return false;
        }

        var own = $"{request.Scheme}://{request.Host.Value}";
        return string.Equals(origin, own, StringComparison.OrdinalIgnoreCase)
            || (publicOrigin is not null && string.Equals(origin, publicOrigin, StringComparison.OrdinalIgnoreCase));
    }
}
