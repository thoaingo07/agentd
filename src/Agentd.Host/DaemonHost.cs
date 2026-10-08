using Agentd.Bff;
using Agentd.Bff.Http;
using Agentd.Bff.Security;
using Agentd.Host.Workers;
using Agentd.Infrastructure.Claude;
using Agentd.Infrastructure.Persistence;
using Agentd.Mcp;
using Agentd.Web;
using Agentd.Web.Vite;
using Microsoft.AspNetCore.DataProtection;
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
        var home = builder.AddAgentdCore(args);
        // Cookies (antiforgery now, SSO later) stay valid across restarts and upgrades.
        builder.Services.AddDataProtection().SetApplicationName(ConfigHome.ApplicationName).PersistKeysToFileSystem(new DirectoryInfo(home.Keys));
        HostUrls.ApplyDefault(builder, home);   // loopback only until setup is complete
        builder.Services.AddWebHosting(builder.Configuration);
        builder.Services.AddSingleton<IPostConfigureOptions<ClaudeOptions>, McpUrlFromServer>();
        builder.Services.AddAgentdMcp();
        builder.Services.AddBff();

        builder.Services.AddHostedService<SetupLinkAnnouncer>();
        builder.Services.AddHostedService<Control.ControlSocket>();   // ~/.agentd/run/agentd.sock: live status and run for the CLI   // not set up yet: logs the one-time setup link

        // Daemon loop. Recovery is registered first: hosted services start in order, so it completes before the workers run.
        builder.Services.AddHostedService<UserDirectorySeeder>();
        builder.Services.AddSingleton<WorkerStatus>();
        builder.Services.AddSingleton<StartupRecovery>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StartupRecovery>());
        builder.Services.AddHostedService<WorkItemPollingWorker>();
        builder.Services.AddHostedService<SchedulerWorker>();
        builder.Services.AddHostedService<MessagingDispatcherWorker>();
        builder.Services.AddHostedService<HeartbeatWorker>();
        builder.Services.AddHostedService<WorktreeSweepWorker>();
        builder.Services.AddSingleton<Application.Ports.IResourceSampler, ProcResourceSampler>(_ => new ProcResourceSampler());
        builder.Services.AddHostedService<ResourceSamplerWorker>();
        builder.Services.AddEventStreaming();
        builder.Services.AddHostedService<ReviewMonitorWorker>();
        builder.Services.AddSingleton<MessagingProviderHealthCheck>();   // one instance keeps the 30 s cache
        builder.Services.AddHealthChecks()
            .AddCheck<WorkerHealthCheck>("workers")
            .AddCheck<MessagingProviderHealthCheck>("messaging");

        var app = builder.Build();

        app.UseBffForwardedHeaders();             // Cloudflare Tunnel only: https + visitor IP from cloudflared
        app.UseSecurityHeaders(o => o.AllowInlineStylesForDevServer = app.Services.GetRequiredService<ViteHelper>().UsesDevServer);   // first: every response

        app.UseAgentdMcpOriginGuard();            // browsers never reach /mcp
        app.UseBffHubOriginGuard();               // /hubs: same-origin browsers only

        app.MapDefaultEndpoints();
        app.MapAgentdMcp();                       // per-job bearer tokens, agents only
        app.MapBff();                             // /api for the Vue app (the local user until SSO)
        app.UseWebHosting(afterRouting: pipeline =>   // Razor shells for ClientApps/* (Vite manifest in production, dev-server proxy in Development)
        {
            pipeline.UseAuthentication();
            pipeline.UseAuthorization();
            pipeline.UseRateLimiter();
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
