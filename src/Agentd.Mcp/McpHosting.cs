using Agentd.Application.Ports;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.AspNetCore;

namespace Agentd.Mcp;

public static class McpHosting
{
    public const string Path = "/mcp";
    public const string Policy = "McpJob";

    /// <summary>MCP server (stateless Streamable HTTP), per-job bearer auth and the token issuer.</summary>
    public static IServiceCollection AddAgentdMcp(this IServiceCollection services)
    {
        services.TryAddSingleton<IMcpTokenIssuer, McpTokenIssuer>();
        services.AddHttpContextAccessor();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, McpJobAuthenticationHandler>(McpJobAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, p => p
                .AddAuthenticationSchemes(McpJobAuthenticationHandler.SchemeName)
                .RequireClaim(McpJobAuthenticationHandler.JobIdClaim));
        services.AddMcpServer()
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<AgentdTools>();
        return services;
    }

    /// <summary>
    /// Refuses browser requests to <c>/mcp</c> (any <c>Origin</c> header) before authentication runs:
    /// only local agent processes call this endpoint. Call early in the pipeline.
    /// </summary>
    public static IApplicationBuilder UseAgentdMcpOriginGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            // Browsers always send Origin; a tunnel (cloudflared) adds Cf-Connecting-IP. /mcp is for local agents only.
            if (context.Request.Path.StartsWithSegments(Path, StringComparison.Ordinal)
                && (context.Request.Headers.Origin.Count > 0 || context.Request.Headers.ContainsKey("Cf-Connecting-IP")))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });

    /// <summary>Maps <c>/mcp</c> behind the per-job bearer policy.</summary>
    public static IEndpointConventionBuilder MapAgentdMcp(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMcp(Path).RequireAuthorization(Policy);
}
