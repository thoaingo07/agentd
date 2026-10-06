using Agentd.Application.Abstractions;
using Agentd.Application.Queries;
using Agentd.Bff.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary><c>GET /api/config</c>: the settings summary (Settings, dashboard); <c>GET /api/resources</c>: CPU, RAM and disk use.</summary>
public static class ConfigEndpoints
{
    public static RouteGroupBuilder MapConfig(this RouteGroupBuilder api)
    {
        api.MapGet("/config", async ([FromServices] IQueryHandler<GetConfigSummary, ConfigSummary> handler, CancellationToken ct) =>
            TypedResults.Ok(ConfigVm.From(await handler.Handle(new GetConfigSummary(), ct).ConfigureAwait(false))))
            .WithName("GetConfig");

        // Polled by the Web UI (every 10 s while a page shows it): the machine and each running job's CPU, RAM and disk.
        api.MapGet("/resources", async ([FromServices] IQueryHandler<GetResources, ResourcesView> handler, CancellationToken ct) =>
            TypedResults.Ok(ResourcesVm.From(await handler.Handle(new GetResources(), ct).ConfigureAwait(false))))
            .WithName("GetResources");
        return api;
    }
}
