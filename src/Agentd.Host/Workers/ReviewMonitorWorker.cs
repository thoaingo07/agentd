using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Workers;

/// <summary>
/// Every <see cref="JobOptions.ReviewPollInterval"/> while the scheduler is enabled: <see cref="ReviewPullRequests"/> for agentd's own PRs
/// (with <see cref="JobOptions.ReviewLoop"/>), and the re-checks of posted <c>!review</c>s.
/// </summary>
internal sealed partial class ReviewMonitorWorker(
    IServiceScopeFactory scopes,
    IOptions<JobOptions> jobOptions,
    IOptions<SchedulerOptions> schedulerOptions,
    ILogger<ReviewMonitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!schedulerOptions.Value.Enabled)
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
                        if (jobOptions.Value.ReviewLoop)
                        {
                            await scope.ServiceProvider.GetRequiredService<ICommandHandler<ReviewPullRequests, int>>()
                                .Handle(new ReviewPullRequests(), stoppingToken).ConfigureAwait(false);
                        }

                        await scope.ServiceProvider.GetRequiredService<Application.Reviews.ReviewService>().MonitorAsync(stoppingToken).ConfigureAwait(false);
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
