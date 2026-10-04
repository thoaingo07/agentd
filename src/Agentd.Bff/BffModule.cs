using System.Text.Json.Serialization;
using Agentd.Bff.Endpoints;
using Agentd.Bff.Http;
using Agentd.Bff.OpenApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentd.Bff;

/// <summary>The screen-shaped API for the Vue app (<c>/api</c>). The Host calls <see cref="AddBff"/> and <see cref="MapBff"/>.</summary>
public static class BffModule
{
    public const string DocumentName = "v1";

    public static IServiceCollection AddBff(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddOpenApi(DocumentName, o => o.AddDocumentTransformer<BffOnlyDocumentTransformer>());
        // Numbers are numbers in the contract (the Web default also accepts them as strings, which types them `number | string`).
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
        services.AddAuthentication(LocalUserAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LocalUserAuthenticationHandler>(LocalUserAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization();
        return services;
    }

    /// <summary>Maps <c>/api/*</c>, all requiring an authenticated user (the local user until Phase 5).</summary>
    public static RouteGroupBuilder MapBff(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            // The API shape is only published in Development (and to Admins later).
            endpoints.MapOpenApi("/openapi/{documentName}.json");
        }

        var api = endpoints.MapGroup("/api").RequireAuthorization();
        api.MapJobReads();
        api.MapJobActions();
        return api;
    }
}
