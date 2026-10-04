using System.Security.Claims;
using Agentd.Bff.ViewModels;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary><c>/bff</c>: the browser session (who am I, the antiforgery token).</summary>
public static class SessionEndpoints
{
    public static RouteGroupBuilder MapSession(this RouteGroupBuilder bff)
    {
        // The request token goes in the body (kept in memory by the SPA); its cookie half is HttpOnly.
        bff.MapGet("/antiforgery", (HttpContext http, [FromServices] IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new AntiforgeryTokenVm(tokens.RequestToken!));
        }).WithName("GetAntiforgeryToken");

        bff.MapGet("/user", (ClaimsPrincipal user) => TypedResults.Ok(new UserVm(
            user.Identity?.Name ?? string.Empty,
            user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList(),
            user.Identity?.AuthenticationType switch
            {
                Http.LocalUserAuthenticationHandler.SchemeName => "local",
                Security.CloudflareAccessAuthenticationHandler.SchemeName => "cloudflare",
                var other => other ?? string.Empty,
            })))
            .WithName("GetUser");

        return bff;
    }
}
