using Agentd.Bff.Endpoints;
using Agentd.Bff.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff;

/// <summary>The screen-shaped API for the Vue app (<c>/api</c>). The Host calls <see cref="AddBff"/> and <see cref="MapBff"/>.</summary>
public static class BffModule
{
    public static IServiceCollection AddBff(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddAuthentication(LocalUserAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LocalUserAuthenticationHandler>(LocalUserAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization();
        return services;
    }

    /// <summary>Maps <c>/api/*</c>, all requiring an authenticated user (the local user until Phase 5).</summary>
    public static RouteGroupBuilder MapBff(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").RequireAuthorization();
        api.MapJobReads();
        return api;
    }
}
