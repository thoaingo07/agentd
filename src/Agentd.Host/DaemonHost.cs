using Agentd.Bff;
using Agentd.Bff.Http;
using Agentd.Bff.Security;
using Agentd.Host.Workers;
using Agentd.Infrastructure.Claude;
using Agentd.Infrastructure.Persistence;
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
        var home = builder.AddAgentdCore(args);
        HostUrls.ApplyDefault(builder, home);   // loopback only until setup is complete
        builder.Services.AddWebHosting(builder.Configuration);
        builder.Services.AddSingleton<IPostConfigureOptions<ClaudeOptions>, McpUrlFromServer>();
        builder.Services.AddAgentdMcp();
        builder.Services.AddBff();

        // Settings → System log: the daemon's own log, stored in batches (the console and journald keep theirs).
        builder.Services.AddSingleton<Logging.SystemLogWriter>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Logging.SystemLogWriter>());
        builder.Services.AddSingleton<ILoggerProvider, Logging.SystemLogProvider>();

        builder.Services.AddSingleton<DaemonRestart>();
        builder.Services.AddSingleton<Application.Setup.IDaemonRestart>(sp => sp.GetRequiredService<DaemonRestart>());
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
        app.UseSecurityHeaders();                 // first: every response

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

        return await RunUntilStoppedAsync(app, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the host until it stops; <see cref="Cli.ExitCodes.Restart"/> when Settings asked for a restart, so the supervisor
    /// starts agentd again. <c>RunAsync</c> disposes the host's services when it returns, so the restart is looked up first.
    /// </summary>
    internal static async Task<int> RunUntilStoppedAsync(WebApplication app, CancellationToken cancellationToken)
    {
        var restart = app.Services.GetRequiredService<DaemonRestart>();
        await app.RunAsync(cancellationToken).ConfigureAwait(false);
        return restart.Requested ? Cli.ExitCodes.Restart : Cli.ExitCodes.Ok;
    }

    /// <summary>
    /// The current directory when it holds the app's settings (<c>dotnet run</c>, Aspire, tests);
    /// otherwise the binary's own directory, so <c>agentd</c> started from anywhere (systemd, a shell)
    /// still reads its <c>appsettings*.json</c>.
    /// </summary>
    internal static string? ContentRoot() =>
        File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json")) ? null : AppContext.BaseDirectory;
}
