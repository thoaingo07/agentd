using Agentd.Application;
using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Application.Repositories;
using Agentd.Application.Users;
using Agentd.Host.Configuration;
using Agentd.Host.Options;
using Agentd.Infrastructure.AzureDevOps;
using Agentd.Infrastructure.Claude;
using Agentd.Infrastructure.Git;
using Agentd.Infrastructure.Messaging.Discord;
using Agentd.Infrastructure.Persistence;
using Agentd.Mcp;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agentd.Host;

/// <summary>Services shared by the daemon and the CLI verbs.</summary>
internal static class AgentdServices
{
    public static ConfigHome AddAgentdCore(this IHostApplicationBuilder builder, string[] args)
    {
        var home = ConfigHome.Resolve().EnsureCreated();
        builder.Configuration.AddConfigHome(home, args);
        builder.Services.AddSingleton(home);
        builder.Services.AddSingleton<Application.Setup.ISetupState>(sp => new Configuration.SetupState(home, sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton(new Bff.Setup.SetupToken(home.SetupTokenFile));
        builder.Services.AddSingleton<Application.Setup.ISetupLink>(sp => sp.GetRequiredService<Bff.Setup.SetupToken>());
        builder.Services.AddSingleton<Application.Setup.IConfigWriter>(sp => new Configuration.AgentdJsonFile(home, sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<Application.Setup.ISecrets>(new Configuration.StoredSecrets(new Configuration.SecretStore(home)));
        builder.Services.AddSingleton<Application.Setup.ISettingsAudit, Configuration.FileSettingsAudit>();
        builder.Services.AddSingleton<Application.Setup.IDatabaseProbe, Setup.NpgsqlDatabaseProbe>();

        builder.Services.AddOptions<AgentdOptions>()
            .BindConfiguration(AgentdOptions.Section)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<AgentdOptions>, AgentdOptionsValidator>();

        // PostgreSQL: pooled NpgsqlDataSource with health check and tracing (connection string "agentd").
        builder.AddNpgsqlDataSource("agentd");
        builder.Services.AddSingleton<Domain.Common.IClock, SystemClock>();
        builder.Services.AddPersistence();

        // Use cases and their adapters: Azure DevOps (az login by default), git worktrees, Claude Code (subscription).
        builder.Services.AddOptions<JobOptions>().BindConfiguration(JobOptions.Section);
        builder.Services.AddOptions<SchedulerOptions>().BindConfiguration(SchedulerOptions.Section);
        builder.Services.AddOptions<RepositorySeedOptions>().BindConfiguration(RepositorySeedOptions.Section);
        builder.Services.AddOptions<MessagingOptions>().BindConfiguration(MessagingOptions.Section).ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<MessagingOptions>, MessagingOptionsValidator>();
        builder.Services.AddOptions<UsersOptions>()
            .Configure<IConfiguration>((o, config) => config.GetSection(UsersOptions.Section).Bind(o.Items))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<UsersOptions>, UsersOptionsValidator>();
        builder.Services.AddApplication();
        builder.Services.AddAzureDevOps(builder.Configuration);
        builder.Services.AddGit(builder.Configuration);
        builder.Services.AddClaude(builder.Configuration);
        builder.Services.AddDiscordMessaging(builder.Configuration);
        // The agent runner issues per-job MCP tokens; CLI verbs resolve it too (e.g. cancel), not only the daemon.
        builder.Services.TryAddSingleton<IMcpTokenIssuer, McpTokenIssuer>();
        return home;
    }
}
