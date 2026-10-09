using System.Security.Claims;
using System.Text.Json;
using Agentd.Application.AzureDevOps;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace Agentd.Bff.Endpoints;

/// <summary>
/// Connect a person's Azure DevOps (docs/architect/ado-user-delegation.md §2): <c>/bff/ado/connect</c> sends the browser
/// to Microsoft with a one-time state, <c>/bff/ado/callback</c> finishes it; <c>/api/me/ado-connections</c> lists and
/// removes the person's own. Every result lands back on <c>/settings?ado=…</c>.
/// </summary>
public static class AdoConnectEndpoints
{
    public const string CallbackPath = "/bff/ado/callback";
    public const string StateCookie = "agentd.ado-connect";
    private const string Purpose = "agentd.ado-connect-state";

    public static RouteGroupBuilder MapAdoConnect(this RouteGroupBuilder bff)
    {
        bff.MapGet("/ado/connect", (HttpContext http, IConfiguration configuration, [FromServices] AdoConnections connections, [FromServices] IDataProtectionProvider keys) =>
        {
            if (!connections.IsConfigured)
            {
                return Results.Redirect("/settings?ado=unavailable");
            }

            var start = connections.Start(Callback(http, configuration));
            // Lax: the callback is a top-level GET from Microsoft's sign-in page, so the cookie comes back with it.
            http.Response.Cookies.Append(StateCookie, keys.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(new[] { start.State, start.CodeVerifier })), new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/bff/ado",
                MaxAge = TimeSpan.FromMinutes(10),
                IsEssential = true,
            });
            return Results.Redirect(start.AuthorizeUrl.ToString());
        }).ExcludeFromDescription();

        bff.MapGet("/ado/callback", async (HttpContext http, ClaimsPrincipal user, IConfiguration configuration, string? code, string? state, string? error,
            [FromServices] AdoConnections connections, [FromServices] IDataProtectionProvider keys, CancellationToken ct) =>
        {
            var started = Started(http, keys);
            http.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/bff/ado" });
            if (!string.IsNullOrEmpty(error))
            {
                return Back("error", error == "access_denied" ? "You cancelled the sign-in, or didn't consent." : $"Microsoft said: {error}.");
            }

            var result = await connections.CompleteAsync(Login(user), code, state, started, Callback(http, configuration), ct).ConfigureAwait(false);
            return result.IsSuccess ? Back("connected", null) : Back("error", result.Error!.Message);
        }).ExcludeFromDescription();

        return bff;
    }

    public static RouteGroupBuilder MapMyAdoConnections(this RouteGroupBuilder api)
    {
        api.MapGet("/me/ado-connections", async (ClaimsPrincipal user, [FromServices] AdoConnections connections, CancellationToken ct) =>
            TypedResults.Ok(new AdoConnectionsVm(connections.IsConfigured,
                [.. (await connections.MineAsync(Login(user), ct).ConfigureAwait(false)).Select(c => new AdoConnectionVm(c.IdentityId, c.UniqueName, c.DisplayName, c.Failed, c.LastError, c.ConnectedAt))])))
            .WithName("GetMyAdoConnections");

        api.MapDelete("/me/ado-connections/{id:guid}", async Task<IResult> (Guid id, ClaimsPrincipal user, [FromServices] AdoConnections connections, CancellationToken ct) =>
            await connections.DisconnectAsync(Login(user), id, ct).ConfigureAwait(false) ? TypedResults.NoContent() : TypedResults.NotFound())
            .WithName("DisconnectMyAdoConnection");
        return api;
    }

    /// <summary>The person's agentd login (Tailscale / Cloudflare Access email, or "local").</summary>
    internal static string Login(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? "local";

    /// <summary>The callback on the URL people use (<c>Agentd:Web:PublicOrigin</c> behind a proxy): it must match the app registration.</summary>
    internal static Uri Callback(HttpContext http, IConfiguration configuration) =>
        new((configuration["Agentd:Web:PublicOrigin"]?.TrimEnd('/') ?? $"{http.Request.Scheme}://{http.Request.Host}") + CallbackPath);

    private static (string State, string CodeVerifier)? Started(HttpContext http, IDataProtectionProvider keys)
    {
        try
        {
            return http.Request.Cookies[StateCookie] is { } cookie && JsonSerializer.Deserialize<string[]>(keys.CreateProtector(Purpose).Unprotect(cookie)) is [var state, var verifier]
                ? (state, verifier)
                : null;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;   // tampered with, or from other keys: start again
        }
    }

    private static IResult Back(string outcome, string? reason) =>
        Results.Redirect($"/settings?ado={outcome}{(reason is null ? string.Empty : "&reason=" + Uri.EscapeDataString(reason))}");
}

/// <summary>A person's Azure DevOps connection (never the token). <c>failed</c>: reconnect (<c>lastError</c> says why).</summary>
public sealed record AdoConnectionVm(Guid IdentityId, string UniqueName, string DisplayName, bool Failed, string? LastError, DateTimeOffset ConnectedAt);

/// <summary><c>available</c>: the Entra app is set up (Settings → Azure DevOps → Service principal), so people can connect.</summary>
public sealed record AdoConnectionsVm(bool Available, IReadOnlyList<AdoConnectionVm> Connections);
