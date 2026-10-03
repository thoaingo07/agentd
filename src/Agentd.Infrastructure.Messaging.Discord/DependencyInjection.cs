using Agentd.Application.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Messaging.Discord;

public static class DependencyInjection
{
    public const string HttpClientName = "discord";

    /// <summary>Registers the Discord provider (it only becomes active when enabled in <c>Agentd:Messaging</c>).</summary>
    public static IServiceCollection AddDiscordMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DiscordOptions>().Bind(configuration.GetSection(DiscordOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<DiscordOptions>, DiscordOptionsValidator>();
        services.AddTransient<DiscordAuthHandler>();
        services.AddHttpClient(HttpClientName, (sp, http) =>
                http.BaseAddress = sp.GetRequiredService<IOptions<DiscordOptions>>().Value.ApiBaseUrl)
            .AddHttpMessageHandler<DiscordAuthHandler>();
        services.AddSingleton(sp => new DiscordRest(() => sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));
        services.AddSingleton<DiscordMessagingProvider>();
        services.AddSingleton<IMessagingProvider>(sp => sp.GetRequiredService<DiscordMessagingProvider>());
        return services;
    }
}
