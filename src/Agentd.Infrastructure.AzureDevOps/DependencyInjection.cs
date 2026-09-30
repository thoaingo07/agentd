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
        services.AddSingleton<IAdoAuthProvider>(sp => sp.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value.Auth == AzureDevOpsAuth.Pat
            ? new PatAuthProvider(sp.GetRequiredService<IOptions<AzureDevOpsOptions>>())
            : new AzCliAuthProvider());
        services.AddTransient<AdoAuthHandler>();

        // One named client for both typed clients: configuring the same name twice would stack the auth
        // handler twice (duplicate headers, which Azure DevOps then ignores).
        // Resilience (retries, 429 Retry-After, timeouts) comes from the host's HttpClient defaults.
        // Never follow redirects: a redirect means "sign in", i.e. the credential was rejected.
        services.AddHttpClient(HttpClientName, Configure)
            .ConfigurePrimaryHttpMessageHandler(NoRedirects)
            .AddHttpMessageHandler<AdoAuthHandler>()
            .AddTypedClient<IWorkItemSource, AzureDevOpsWorkItemSource>()
            .AddTypedClient<IPullRequestService, AzureDevOpsPullRequests>();
        return services;

        static HttpMessageHandler NoRedirects() => new SocketsHttpHandler { AllowAutoRedirect = false };

        static void Configure(IServiceProvider sp, HttpClient http) =>
            http.BaseAddress = sp.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value.BaseUrl;
    }
}
