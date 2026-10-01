using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Domain.Common;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Workers;

/// <summary>Polls Azure DevOps for tagged work items and claims them; backs off exponentially on failures.</summary>
internal sealed partial class WorkItemPollingWorker(
    IServiceScopeFactory scopes,
    JobDispatcher dispatcher,
    WorkerStatus status,
    IClock clock,
    IOptions<SchedulerOptions> options,
    ILogger<WorkItemPollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = await PollOnceAsync(o, stoppingToken).ConfigureAwait(false);
            status.NextPollAt = clock.UtcNow + delay;
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<TimeSpan> PollOnceAsync(SchedulerOptions o, CancellationToken ct)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var claimed = await scope.ServiceProvider.GetRequiredService<ICommandHandler<PollWorkItems, int>>()
                    .Handle(new PollWorkItems(), ct).ConfigureAwait(false);
                status.LastPollAt = clock.UtcNow;
                status.ConsecutivePollFailures = 0;
                status.LastPollError = null;
                if (claimed.Value > 0)
                {
                    LogClaimed(logger, claimed.Value);
                    dispatcher.Signal();
                }
            }

            return o.PollInterval;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            status.ConsecutivePollFailures++;
            status.LastPollError = ex.Message;
            var backoff = Backoff(o, status.ConsecutivePollFailures);
            LogPollFailed(logger, ex, status.ConsecutivePollFailures, backoff);
            return backoff;
        }
    }

    /// <summary>PollInterval × 2^failures, capped at MaxPollBackoff (never shorter than PollInterval).</summary>
    internal static TimeSpan Backoff(SchedulerOptions o, int failures)
    {
        var factor = Math.Pow(2, Math.Min(failures, 16));
        var ticks = Math.Min(o.PollInterval.Ticks * factor, o.MaxPollBackoff.Ticks);
        return TimeSpan.FromTicks((long)Math.Max(ticks, o.PollInterval.Ticks));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Claimed {Count} work item(s)")]
    private static partial void LogClaimed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling work items failed ({Failures} in a row); next attempt in {Delay}")]
    private static partial void LogPollFailed(ILogger logger, Exception exception, int failures, TimeSpan delay);
}
