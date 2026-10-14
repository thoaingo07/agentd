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
/// starts it again) and what's running now, <c>POST</c> stops it for that. While jobs or agent turns run, <c>POST</c> answers
/// 409 with them unless <c>?force=true</c> (an Admin's "restart anyway"). See <see cref="IDaemonRestart"/> and <see cref="RestartCheck"/>.
/// </summary>
public static class RestartEndpoints
{
    public static RouteGroupBuilder MapRestart(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var restart = api.MapGroup("/settings/restart").RequireAuthorization(p => p.RequireRole("Admin"));
        restart.MapGet("/", async ([FromServices] IServiceProvider services, CancellationToken ct) =>
                TypedResults.Ok(RestartVm.From(Availability(services), await RunningAsync(services, ct).ConfigureAwait(false))))
            .WithName("GetRestart");
        restart.MapPost("/", async Task<IResult> (bool? force, ClaimsPrincipal user, [FromServices] IServiceProvider services, CancellationToken ct) =>
            {
                if (services.GetService<IDaemonRestart>() is not { Availability.Available: true } daemon)
                {
                    return TypedResults.Problem(Availability(services).Reason, statusCode: StatusCodes.Status409Conflict);
                }

                var running = await RunningAsync(services, ct).ConfigureAwait(false);
                if (running.Count > 0 && force != true)
                {
                    return TypedResults.Problem($"Running now: {string.Join("; ", running.Select(r => r.Label))}. Restart anyway to stop them; they pick up where they were once agentd is back.",
                        statusCode: StatusCodes.Status409Conflict, title: "Work is running");
                }

                return await daemon.RequestAsync(user.Identity?.Name ?? "admin", ct).ConfigureAwait(false)
                    ? TypedResults.Accepted((string?)null, RestartVm.From(daemon.Availability, running))
                    : TypedResults.Problem(daemon.Availability.Reason, statusCode: StatusCodes.Status409Conflict);
            })
            .WithName("RestartDaemon").Produces<RestartVm>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status409Conflict);
        return api;
    }

    private static async Task<IReadOnlyList<RunningWork>> RunningAsync(IServiceProvider services, CancellationToken ct) =>
        services.GetService<RestartCheck>() is { } check ? await check.RunningAsync(ct).ConfigureAwait(false) : [];

    private static RestartAvailability Availability(IServiceProvider services) =>
        services.GetService<IDaemonRestart>()?.Availability ?? new RestartAvailability(false, "This host can't restart agentd.");
}

/// <summary>Whether Settings can restart agentd (<c>reason</c> says why not), and what a restart would interrupt now.</summary>
public sealed record RestartVm(bool Available, string? Reason, IReadOnlyList<RunningWorkVm> Running)
{
    public static RestartVm From(RestartAvailability a, IReadOnlyList<RunningWork> running)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(running);
        return new(a.Available, a.Reason, [.. running.Select(r => new RunningWorkVm(r.Source, r.Id, r.Label))]);
    }
}

/// <summary>A job mid-step or an agent's turn: <c>source</c> is job, idea, chat or review.</summary>
public sealed record RunningWorkVm(string Source, long Id, string Label);
