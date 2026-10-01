using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Domain.Common;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Workers;

/// <summary>
/// Resumes recovered runs, then keeps starting queued jobs while slots are free and retries due
/// publishes. Stopping stops new starts, then gives running agents <see cref="SchedulerOptions.ShutdownGrace"/>.
/// </summary>
internal sealed partial class SchedulerWorker(
    IServiceScopeFactory scopes,
    JobDispatcher dispatcher,
    StartupRecovery recovery,
    IClock clock,
    IOptions<SchedulerOptions> options,
    ILogger<SchedulerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan s_publishRetryCheck = TimeSpan.FromSeconds(30);
    private DateTimeOffset _lastPublishRetryCheck = DateTimeOffset.MinValue;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await dispatcher.StopAsync().ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            return;
        }

        try
        {
            foreach (var resume in recovery.Resumes)
            {
                await dispatcher.ResumeAsync(resume, stoppingToken).ConfigureAwait(false);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!await dispatcher.TryStartNextAsync(stoppingToken).ConfigureAwait(false))
                    {
                        await RetryDuePublishesAsync(stoppingToken).ConfigureAwait(false);
                        await dispatcher.WaitForWorkAsync(o.IdleDelay, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogLoopFailed(logger, ex);
                    await Task.Delay(o.IdleDelay * 5, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task RetryDuePublishesAsync(CancellationToken ct)
    {
        if (clock.UtcNow - _lastPublishRetryCheck < s_publishRetryCheck)
        {
            return;
        }

        _lastPublishRetryCheck = clock.UtcNow;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var retried = await scope.ServiceProvider.GetRequiredService<ICommandHandler<RetryDuePublishes, int>>()
                .Handle(new RetryDuePublishes(), ct).ConfigureAwait(false);
            if (retried.Value > 0)
            {
                LogPublishRetried(logger, retried.Value);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduler loop failed; retrying")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retried publishing for {Count} job(s)")]
    private static partial void LogPublishRetried(ILogger logger, int count);
}
