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

        // Resilience (retries, 429 Retry-After, timeouts) comes from the host's HttpClient defaults.
        // Never follow redirects: a redirect means "sign in", i.e. the credential was rejected.
        services.AddHttpClient<IWorkItemSource, AzureDevOpsWorkItemSource>(HttpClientName, Configure)
            .ConfigurePrimaryHttpMessageHandler(NoRedirects).AddHttpMessageHandler<AdoAuthHandler>();
        services.AddHttpClient<IPullRequestService, AzureDevOpsPullRequests>(HttpClientName, Configure)
            .ConfigurePrimaryHttpMessageHandler(NoRedirects).AddHttpMessageHandler<AdoAuthHandler>();
        return services;

        static HttpMessageHandler NoRedirects() => new SocketsHttpHandler { AllowAutoRedirect = false };

        static void Configure(IServiceProvider sp, HttpClient http) =>
            http.BaseAddress = sp.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value.BaseUrl;
    }
}
