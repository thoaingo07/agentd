using Agentd.Application.Abstractions;
using Agentd.Application.Queries;
using Agentd.Bff.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary><c>GET /api/config</c>: the settings summary for the Settings page and the dashboard (max concurrency, work item links).</summary>
public static class ConfigEndpoints
{
    public static RouteGroupBuilder MapConfig(this RouteGroupBuilder api)
    {
        api.MapGet("/config", async ([FromServices] IQueryHandler<GetConfigSummary, ConfigSummary> handler, CancellationToken ct) =>
            TypedResults.Ok(ConfigVm.From(await handler.Handle(new GetConfigSummary(), ct).ConfigureAwait(false))))
            .WithName("GetConfig");
        return api;
    }
}
