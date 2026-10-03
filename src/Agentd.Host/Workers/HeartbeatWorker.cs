using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Workers;

/// <summary>Runs <see cref="PostHeartbeats"/> every <see cref="JobOptions.HeartbeatInterval"/> while a chat provider is enabled.</summary>
internal sealed partial class HeartbeatWorker(
    IServiceScopeFactory scopes,
    IMessagingProviderRegistry providers,
    IOptions<JobOptions> options,
    ILogger<HeartbeatWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (providers.Enabled.Count == 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var scope = scopes.CreateAsyncScope();
                    await using (scope.ConfigureAwait(false))
                    {
                        await scope.ServiceProvider.GetRequiredService<ICommandHandler<PostHeartbeats, int>>()
                            .Handle(new PostHeartbeats(), stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Posting heartbeats failed; retrying next minute")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
