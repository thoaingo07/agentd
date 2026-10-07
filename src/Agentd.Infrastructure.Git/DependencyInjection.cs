using Agentd.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Infrastructure.Git;

public static class DependencyInjection
{
    public static IServiceCollection AddGit(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GitOptions>().Bind(configuration.GetSection(GitOptions.Section));
        services.AddSingleton<GitCli>();
        services.AddSingleton<IGitRemote, GitRemote>();
        services.AddSingleton<IWorktreeManager, GitWorktreeManager>();
        services.AddSingleton<Application.Setup.IGitKey, SshGitKey>();
        return services;
    }
}
