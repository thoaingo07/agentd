using Agentd.Application.Incidents;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Workers;

/// <summary>
/// Every <see cref="IncidentMonitorOptions.PollInterval"/> while <c>Agentd:Monitor:Enabled</c>: one <see cref="IncidentMonitor"/> pass
/// over the configured Application Insights resources (docs/architect/incident-monitor.md). Independent of the job scheduler.
/// </summary>
internal sealed partial class IncidentMonitorWorker(IncidentMonitor monitor, IOptions<IncidentMonitorOptions> options, ILogger<IncidentMonitorWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled || o.Apps.Count == 0)
        {
            return;
        }

        LogStarted(logger, o.Apps.Count, o.PollInterval);
        using var timer = new PeriodicTimer(o.PollInterval);
        try
        {
            do
            {
                try
                {
                    await monitor.PassAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Incident monitor: watching {Apps} app(s) every {Interval}")]
    private static partial void LogStarted(ILogger logger, int apps, TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident monitor pass failed; retrying next interval")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
