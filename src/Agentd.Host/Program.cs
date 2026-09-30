using Agentd.Application;
using Agentd.Application.Jobs;
using Agentd.Application.Repositories;
using Agentd.Host;
using Agentd.Host.Options;
using Agentd.Host.Workers;
using Agentd.Infrastructure.AzureDevOps;
using Agentd.Infrastructure.Claude;
using Agentd.Infrastructure.Git;
using Agentd.Infrastructure.Persistence;
using Agentd.Mcp;
using Agentd.Web;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Serve Razor class library static assets (Agentd.Web's Vite build under /_content/Agentd.Web/) in every environment.
builder.WebHost.UseStaticWebAssets();

builder.AddServiceDefaults();

builder.Services.AddOptions<AgentdOptions>()
    .BindConfiguration(AgentdOptions.Section)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AgentdOptions>, AgentdOptionsValidator>();

HostUrls.ApplyDefault(builder);

// PostgreSQL: pooled NpgsqlDataSource with health check and tracing (connection string "agentd").
builder.AddNpgsqlDataSource("agentd");
builder.Services.AddSingleton<Agentd.Domain.Common.IClock, SystemClock>();
builder.Services.AddPersistence();
builder.Services.AddWebHosting(builder.Configuration);

// Use cases and their adapters: Azure DevOps (az login by default), git worktrees, Claude Code (subscription).
builder.Services.AddOptions<JobOptions>().BindConfiguration(JobOptions.Section);
builder.Services.AddOptions<SchedulerOptions>().BindConfiguration(SchedulerOptions.Section);
builder.Services.AddOptions<RepositorySeedOptions>().BindConfiguration(RepositorySeedOptions.Section);
builder.Services.AddApplication();
builder.Services.AddAzureDevOps(builder.Configuration);
builder.Services.AddGit(builder.Configuration);
builder.Services.AddClaude(builder.Configuration);
builder.Services.AddSingleton<IPostConfigureOptions<ClaudeOptions>, McpUrlFromServer>();
builder.Services.AddAgentdMcp();

// Daemon loop. Recovery is registered first: hosted services start in order, so it completes before the workers run.
builder.Services.AddSingleton<WorkerStatus>();
builder.Services.AddSingleton<StartupRecovery>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StartupRecovery>());
builder.Services.AddHostedService<WorkItemPollingWorker>();
builder.Services.AddHostedService<SchedulerWorker>();
builder.Services.AddHealthChecks().AddCheck<WorkerHealthCheck>("workers");

var app = builder.Build();

app.UseAgentdMcpOriginGuard();            // browsers never reach /mcp

app.MapDefaultEndpoints();
app.MapAgentdMcp();                       // per-job bearer tokens, agents only
app.UseWebHosting(afterRouting: pipeline =>   // Razor shells for ClientApps/* (Vite manifest in production, dev-server proxy in Development)
{
    pipeline.UseAuthentication();
    pipeline.UseAuthorization();
});

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed for <c>WebApplicationFactory&lt;Program&gt;</c> in tests.</summary>
public partial class Program;
