using Agentd.Host.Workers;
using Agentd.Infrastructure.Claude;
using Agentd.Mcp;
using Agentd.Web;
using Microsoft.Extensions.Options;

namespace Agentd.Host;

/// <summary><c>agentd daemon run</c>: the web host (UI, BFF, MCP) and the daemon loop.</summary>
internal static class DaemonHost
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = ContentRoot() });

        // Serve Razor class library static assets (Agentd.Web's Vite build under /_content/Agentd.Web/) in every environment.
        builder.WebHost.UseStaticWebAssets();

        builder.AddServiceDefaults();
        builder.AddAgentdCore(args);
        HostUrls.ApplyDefault(builder);
        builder.Services.AddWebHosting(builder.Configuration);
        builder.Services.AddSingleton<IPostConfigureOptions<ClaudeOptions>, McpUrlFromServer>();
        builder.Services.AddAgentdMcp();

        // Daemon loop. Recovery is registered first: hosted services start in order, so it completes before the workers run.
        builder.Services.AddHostedService<UserDirectorySeeder>();
        builder.Services.AddSingleton<WorkerStatus>();
        builder.Services.AddSingleton<StartupRecovery>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StartupRecovery>());
        builder.Services.AddHostedService<WorkItemPollingWorker>();
        builder.Services.AddHostedService<SchedulerWorker>();
        builder.Services.AddHostedService<MessagingDispatcherWorker>();
        builder.Services.AddHostedService<HeartbeatWorker>();
        builder.Services.AddHostedService<ReviewMonitorWorker>();
        builder.Services.AddSingleton<MessagingProviderHealthCheck>();   // one instance keeps the 30 s cache
        builder.Services.AddHealthChecks()
            .AddCheck<WorkerHealthCheck>("workers")
            .AddCheck<MessagingProviderHealthCheck>("messaging");

        var app = builder.Build();

        app.UseAgentdMcpOriginGuard();            // browsers never reach /mcp

        app.MapDefaultEndpoints();
        app.MapAgentdMcp();                       // per-job bearer tokens, agents only
        app.UseWebHosting(afterRouting: pipeline =>   // Razor shells for ClientApps/* (Vite manifest in production, dev-server proxy in Development)
        {
            pipeline.UseAuthentication();
            pipeline.UseAuthorization();
        });

        await app.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// The current directory when it holds the app's settings (<c>dotnet run</c>, Aspire, tests);
    /// otherwise the binary's own directory, so <c>agentd</c> started from anywhere (systemd, a shell)
    /// still reads its <c>appsettings*.json</c>.
    /// </summary>
    internal static string? ContentRoot() =>
        File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json")) ? null : AppContext.BaseDirectory;
}
