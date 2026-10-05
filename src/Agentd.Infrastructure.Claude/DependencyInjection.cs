using Agentd.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Infrastructure.Claude;

public static class DependencyInjection
{
    public static IServiceCollection AddClaude(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ClaudeOptions>().Bind(configuration.GetSection(ClaudeOptions.Section));
        services.AddSingleton<IAgentRunner, ClaudeCodeRunner>();
        services.AddSingleton<Application.Permissions.IToolAllowlist, ClaudeToolAllowlist>();
        services.AddSingleton<Application.Messaging.ITranscriptReader, TranscriptReader>();
        return services;
    }
}
