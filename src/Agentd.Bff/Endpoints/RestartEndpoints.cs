using System.Security.Claims;
using Agentd.Application.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Endpoints;

/// <summary>
/// Settings → Restart now (Admins): <c>GET /api/settings/restart</c> says whether agentd can restart itself (a supervisor
/// starts it again), <c>POST</c> stops it for that. See <see cref="IDaemonRestart"/>.
/// </summary>
public static class RestartEndpoints
{
    public static RouteGroupBuilder MapRestart(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var restart = api.MapGroup("/settings/restart").RequireAuthorization(p => p.RequireRole("Admin"));
        restart.MapGet("/", ([FromServices] IServiceProvider services) => TypedResults.Ok(RestartVm.From(Availability(services))))
            .WithName("GetRestart");
        restart.MapPost("/", async Task<IResult> (ClaimsPrincipal user, [FromServices] IServiceProvider services, CancellationToken ct) =>
                services.GetService<IDaemonRestart>() is { } daemon && await daemon.RequestAsync(user.Identity?.Name ?? "admin", ct).ConfigureAwait(false)
                    ? TypedResults.Accepted((string?)null, RestartVm.From(daemon.Availability))
                    : TypedResults.Problem(Availability(services).Reason, statusCode: StatusCodes.Status409Conflict))
            .WithName("RestartDaemon").Produces<RestartVm>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status409Conflict);
        return api;
    }

    private static RestartAvailability Availability(IServiceProvider services) =>
        services.GetService<IDaemonRestart>()?.Availability ?? new RestartAvailability(false, "This host can't restart agentd.");
}

/// <summary>Whether Settings can restart agentd; <c>reason</c> says why not.</summary>
public sealed record RestartVm(bool Available, string? Reason)
{
    public static RestartVm From(RestartAvailability a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new(a.Available, a.Reason);
    }
}
