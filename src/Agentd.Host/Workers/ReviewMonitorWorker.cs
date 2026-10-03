using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Workers;

/// <summary>Runs <see cref="ReviewPullRequests"/> every <see cref="JobOptions.ReviewPollInterval"/> while the scheduler is enabled.</summary>
internal sealed partial class ReviewMonitorWorker(
    IServiceScopeFactory scopes,
    IOptions<JobOptions> jobOptions,
    IOptions<SchedulerOptions> schedulerOptions,
    ILogger<ReviewMonitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!schedulerOptions.Value.Enabled || !jobOptions.Value.ReviewLoop)
        {
            return;
        }

        using var timer = new PeriodicTimer(jobOptions.Value.ReviewPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var scope = scopes.CreateAsyncScope();
                    await using (scope.ConfigureAwait(false))
                    {
                        await scope.ServiceProvider.GetRequiredService<ICommandHandler<ReviewPullRequests, int>>()
                            .Handle(new ReviewPullRequests(), stoppingToken).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking pull requests in review failed; retrying next interval")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
