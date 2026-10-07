using System.Security.Claims;
using Agentd.Application.Setup;
using Agentd.Bff.Security;
using Agentd.Bff.ViewModels;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Setup;

/// <summary>
/// Before any login exists, the one-time setup link (<c>GET /setup?token=…</c>) is exchanged for a setup session:
/// an HttpOnly, SameSite=Strict cookie, 30 minutes, sliding. It only opens <c>/api/setup/*</c> (the
/// <see cref="Policy"/>); everything else still needs a normal sign-in. Once setup is complete the link and every
/// session are dead (docs/architect/deployment.md §5a).
/// </summary>
public static class SetupSession
{
    public const string SchemeName = "SetupSession";
    public const string Policy = "Setup";
    public const string CookieName = "agentd.setup";
    public const string ClaimType = "agentd:setup";
    public const string PagePath = "/setup";

    /// <summary>The wizard (the <c>setup</c> SPA, served by Agentd.Web); it reports a missing session itself.</summary>
    public const string WizardPath = "/setup/wizard";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    internal static AuthenticationBuilder AddSetupSession(this AuthenticationBuilder auth) =>
        auth.AddCookie(SchemeName, o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // setup runs on http loopback (an SSH tunnel)
            o.Cookie.Path = "/";
            o.ExpireTimeSpan = Lifetime;
            o.SlidingExpiration = true;
            // An API: 401/403, never a redirect to a login page.
            o.Events.OnRedirectToLogin = c => Status(c.Response, StatusCodes.Status401Unauthorized);
            o.Events.OnRedirectToAccessDenied = c => Status(c.Response, StatusCodes.Status403Forbidden);
            o.Events.OnValidatePrincipal = async c =>
            {
                if (c.HttpContext.RequestServices.GetRequiredService<ISetupState>().IsComplete)
                {
                    c.RejectPrincipal();
                    await c.HttpContext.SignOutAsync(SchemeName).ConfigureAwait(false);
                }
            };
        });

    internal static AuthorizationBuilder AddSetupPolicy(this AuthorizationBuilder authorization) =>
        authorization.AddPolicy(Policy, p => p.AddAuthenticationSchemes(SchemeName).RequireClaim(ClaimType));

    /// <summary>Maps <c>GET /setup</c> (the link) and the <c>/api/setup</c> group, which T1b.11's use cases join.</summary>
    internal static RouteGroupBuilder MapSetupSession(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(PagePath, Exchange).AllowAnonymous().ExcludeFromDescription();

        var setup = endpoints.MapGroup("/api/setup").RequireAuthorization(Policy).AddEndpointFilter<AntiforgeryFilter>();
        setup.MapGet("/session", async (HttpContext http) =>
        {
            var session = await http.AuthenticateAsync(SchemeName).ConfigureAwait(false);
            return TypedResults.Ok(new SetupSessionVm(session.Properties?.ExpiresUtc));
        }).WithName("GetSetupSession");
        setup.MapGet("/antiforgery", (HttpContext http, [FromServices] IAntiforgery antiforgery) =>
        {
            // Bound to the setup session's identity (the /bff one needs a normal sign-in).
            var tokens = antiforgery.GetAndStoreTokens(http);
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new AntiforgeryTokenVm(tokens.RequestToken!));
        }).WithName("GetSetupAntiforgeryToken");
        return setup;
    }

    private static async Task<IResult> Exchange(HttpContext http, [FromQuery] string? token, [FromServices] SetupToken tokens, [FromServices] ISetupState state)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (state.IsComplete)
        {
            return Results.Text("agentd is already set up: sign in as usual. Settings are under Settings in the web UI.", statusCode: StatusCodes.Status404NotFound);
        }

        if (token is not null)
        {
            if (!tokens.Verify(token))
            {
                return Results.Text("This setup link isn't valid (anymore). Run `agentd setup-link` on the server for a new one.", statusCode: StatusCodes.Status403Forbidden);
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "setup"), new Claim(ClaimTypes.Name, "setup"), new Claim(ClaimType, "1")],
                SchemeName);
            await http.SignInAsync(SchemeName, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);
        }

        // The token leaves the address bar (and the history); without a session the wizard says how to get one.
        return Results.Redirect(WizardPath);
    }

    private static Task Status(HttpResponse response, int status)
    {
        response.StatusCode = status;
        return Task.CompletedTask;
    }
}
