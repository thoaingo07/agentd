using System.Security.Claims;
using Agentd.Application.Incidents;
using Agentd.Bff.Http;
using Agentd.Bff.ViewModels;
using Agentd.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Agentd.Bff.Endpoints;

/// <summary>
/// Settings → Monitoring (Admins, docs/architect/incident-monitor.md §4.2): the incident monitor's <c>Agentd:Monitor</c>
/// section in agentd.json (used after a restart), and <b>Test connection</b>, which reads an app's last hour of errors.
/// </summary>
public static class MonitorSettingsEndpoints
{
    public const int MaxApps = 50;
    public const int MaxField = 1024;

    public static RouteGroupBuilder MapMonitorSettings(this RouteGroupBuilder api)
    {
        var monitor = api.MapGroup("/settings/monitor").RequireAuthorization(p => p.RequireRole("Admin"));

        monitor.MapGet("/", ([FromServices] MonitorSettings settings, [FromServices] IOptions<IncidentMonitorOptions> running) =>
                TypedResults.Ok(MonitorStepVm.From(settings.Get(), running.Value.Enabled)))
            .WithName("GetMonitorSettings");

        monitor.MapPut("/", async Task<IResult> (MonitorRequest? body, ClaimsPrincipal user, [FromServices] MonitorSettings settings, CancellationToken ct) =>
        {
            if (Invalid(body?.TenantId, body?.Apps) is { } bad)
            {
                return bad;
            }

            var input = new MonitorInput(body?.Enabled ?? false, body?.TenantId, [.. (body?.Apps ?? []).Select(App)]);
            return (await settings.SaveAsync(input, user.Identity?.Name ?? "admin", ct).ConfigureAwait(false)).ToHttpResult(r => TypedResults.Ok(new SaveResultVm(r.RestartRequired)));
        }).WithName("SaveMonitorSettings").Accepts<MonitorRequest>("application/json").Produces<SaveResultVm>().ProducesProblem(StatusCodes.Status400BadRequest);

        monitor.MapPost("/test", async Task<IResult> (MonitoredAppRequest? body, [FromServices] MonitorSettings settings, CancellationToken ct) =>
        {
            if (body is null || Invalid(null, [body]) is { } bad)
            {
                return DomainError.Validation("Send the app to test: name, resourceId, repository and (optionally) roleName.").ToProblem();
            }

            var check = await settings.TestAsync(App(body), ct).ConfigureAwait(false);
            return TypedResults.Ok(new StepCheckVm(check.Ok, check.Message, check.Fix));
        }).WithName("TestMonitorSettings").Accepts<MonitoredAppRequest>("application/json").Produces<StepCheckVm>().ProducesProblem(StatusCodes.Status400BadRequest);

        return api;
    }

    private static IResult? Invalid(string? tenant, IReadOnlyList<MonitoredAppRequest>? apps) =>
        apps?.Count > MaxApps || (tenant?.Length ?? 0) > MaxField
            || (apps ?? []).Any(a => new[] { a.Name, a.ResourceId, a.Repository, a.RoleName }.Any(f => (f?.Length ?? 0) > MaxField))
            ? DomainError.Validation($"At most {MaxApps} apps, each value at most {MaxField} characters.").ToProblem()
            : null;

    private static MonitoredApp App(MonitoredAppRequest a) =>
        new() { Name = a.Name ?? string.Empty, ResourceId = a.ResourceId ?? string.Empty, Repository = a.Repository ?? string.Empty, RoleName = a.RoleName };

    /// <param name="Enabled">Whether the monitor runs (after a restart).</param>
    /// <param name="TenantId">The Entra directory to sign in to, when it isn't the default one.</param>
    /// <param name="Apps">The whole list, in order: apps left out are removed.</param>
    public sealed record MonitorRequest(bool Enabled, string? TenantId, IReadOnlyList<MonitoredAppRequest>? Apps);

    /// <param name="Name">A short name (letters, digits, '.', '_', '-').</param>
    /// <param name="ResourceId">The Application Insights component's ARM id.</param>
    /// <param name="Repository">The registered repository its code is in.</param>
    /// <param name="RoleName">Only this <c>cloud_RoleName</c>, when several apps share the resource.</param>
    public sealed record MonitoredAppRequest(string? Name, string? ResourceId, string? Repository, string? RoleName);
}

/// <summary>An app the monitor reads.</summary>
public sealed record MonitoredAppVm(string Name, string ResourceId, string Repository, string? RoleName);

/// <summary>
/// Settings → Monitoring as agentd.json has it; <c>running</c> is whether the monitor runs now (it changes after a restart),
/// and <c>enabledOverriddenBy</c> names what sets Enabled over agentd.json.
/// </summary>
public sealed record MonitorStepVm(bool Enabled, string? TenantId, IReadOnlyList<MonitoredAppVm> Apps, string? EnabledOverriddenBy, bool Running)
{
    public static MonitorStepVm From(MonitorStep step, bool running)
    {
        ArgumentNullException.ThrowIfNull(step);
        return new(step.Enabled, step.TenantId, [.. step.Apps.Select(a => new MonitoredAppVm(a.Name, a.ResourceId, a.Repository, a.RoleName))], step.EnabledOverriddenBy, running);
    }
}
