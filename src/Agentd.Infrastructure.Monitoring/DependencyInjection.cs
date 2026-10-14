using Agentd.Application.Incidents;
using Azure.Identity;
using Azure.Monitor.Query.Logs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Monitoring;

public static class DependencyInjection
{
    /// <summary>
    /// The incident monitor's telemetry source. Sign-in is <see cref="DefaultAzureCredential"/> without a browser prompt: a managed
    /// identity, a service principal (<c>AZURE_*</c>), or the az CLI's login; it needs Reader (or Monitoring Reader) on each resource.
    /// </summary>
    public static IServiceCollection AddMonitoring(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var credential = new DefaultAzureCredentialOptions { ExcludeInteractiveBrowserCredential = true };
            if (sp.GetRequiredService<IOptions<IncidentMonitorOptions>>().Value.TenantId is { Length: > 0 } tenant)
            {
                credential.TenantId = tenant;
            }

            return new LogsQueryClient(new DefaultAzureCredential(credential));
        });
        services.AddSingleton<ITelemetrySource, AppInsightsTelemetrySource>();
        return services;
    }
}
