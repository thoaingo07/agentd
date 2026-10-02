using Agentd.Application.Messaging;

namespace Agentd.Host.Workers;

/// <summary>
/// Delivers the messaging outbox: claims due rows every second (sooner while there's a backlog),
/// sweeps rows a crashed dispatcher left in 'sending', and on shutdown gives in-flight sends 10 s.
/// Delivery is at-least-once: a crash between sending and marking a row sent re-sends that message.
/// </summary>
internal sealed partial class MessagingDispatcherWorker(
    OutboxDispatcher dispatcher,
    IOutboxDelivery delivery,
    IMessagingProviderRegistry providers,
    ILogger<MessagingDispatcherWorker> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan s_idle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_staleAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_shutdownGrace = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (providers.Enabled.Count == 0)
        {
            return;
        }

        using var grace = new CancellationTokenSource();
        await using var registration = stoppingToken.Register(() => grace.CancelAfter(s_shutdownGrace));
        try
        {
            // Rows still 'sending' at startup were claimed by the previous process.
            await delivery.ReleaseStaleAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            var lastSweep = DateTimeOffset.UtcNow;
            while (!stoppingToken.IsCancellationRequested)
            {
                var claimed = 0;
                try
                {
                    claimed = await dispatcher.DispatchOnceAsync(BatchSize, grace.Token).ConfigureAwait(false);
                    if (DateTimeOffset.UtcNow - lastSweep > TimeSpan.FromMinutes(1))
                    {
                        lastSweep = DateTimeOffset.UtcNow;
                        await delivery.ReleaseStaleAsync(lastSweep - s_staleAfter, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(logger, ex);
                }

                if (claimed < BatchSize)
                {
                    await Task.Delay(s_idle, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; unfinished rows are released on the next start.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Messaging dispatch failed; retrying")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
