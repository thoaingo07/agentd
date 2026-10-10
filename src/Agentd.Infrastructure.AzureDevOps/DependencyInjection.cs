using Agentd.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.AzureDevOps;

public static class DependencyInjection
{
    public const string HttpClientName = "azure-devops";

    public static IServiceCollection AddAzureDevOps(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AzureDevOpsOptions>().Bind(configuration.GetSection(AzureDevOpsOptions.Section));
        services.AddSingleton<IAdoAuthProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AzureDevOpsOptions>>();
            IAdoAuthProvider own = options.Value.Auth switch
            {
                AzureDevOpsAuth.Pat => new PatAuthProvider(options),
                AzureDevOpsAuth.ServicePrincipal => AzCliAuthProvider.ForServicePrincipal(options.Value.TenantId, options.Value.ClientId, options.Value.ClientSecret),
                _ => new AzCliAuthProvider(),
            };
            // Inside an AdoActor scope, a person's delegated token instead (docs/architect/ado-user-delegation.md).
            return sp.GetService<Application.AzureDevOps.AdoActor>() is { } actor && sp.GetService<Application.AzureDevOps.AdoUserTokens>() is { } tokens
                ? new ActingAsAuthProvider(own, actor, tokens)
                : own;
        });
        services.AddTransient<AdoAuthHandler>();
        // People's delegated sign-in: its own client (their tokens, never agentd's auth handler), no redirects.
        services.AddHttpClient<IAdoDelegation, AdoDelegation>().ConfigurePrimaryHttpMessageHandler(NoRedirects);
        services.AddHttpClient<IAdoPatCheck, AdoPatCheck>().ConfigurePrimaryHttpMessageHandler(NoRedirects);
        services.AddSingleton<Application.Setup.IAzureDevOpsProbe>(new AzureDevOpsProbe());

        // One named client for both typed clients: configuring the same name twice would stack the auth
        // handler twice (duplicate headers, which Azure DevOps then ignores).
        // Resilience (retries, 429 Retry-After, timeouts) comes from the host's HttpClient defaults.
        // Never follow redirects: a redirect means "sign in", i.e. the credential was rejected.
        services.AddHttpClient(HttpClientName, Configure)
            .ConfigurePrimaryHttpMessageHandler(NoRedirects)
            .AddHttpMessageHandler<AdoAuthHandler>()
            .AddTypedClient<IWorkItemSource, AzureDevOpsWorkItemSource>()
            .AddTypedClient<IPullRequestService, AzureDevOpsPullRequests>()
            .AddTypedClient<IAzureDevOpsSearch, AzureDevOpsSearch>();
        return services;

        static HttpMessageHandler NoRedirects() => new SocketsHttpHandler { AllowAutoRedirect = false };

        static void Configure(IServiceProvider sp, HttpClient http) =>
            http.BaseAddress = sp.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value.BaseUrl;
    }
}
