using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Workers;

/// <summary>Runs <see cref="SweepWorktrees"/> shortly after startup, then every <see cref="JobOptions.WorktreeSweepInterval"/>.</summary>
internal sealed partial class WorktreeSweepWorker(
    IServiceScopeFactory scopes,
    IOptions<JobOptions> options,
    ILogger<WorktreeSweepWorker> logger) : BackgroundService
{
    /// <summary>Give startup recovery and the first scheduler pass time first.</summary>
    public static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(options.Value.WorktreeSweepInterval);
            do
            {
                try
                {
                    var scope = scopes.CreateAsyncScope();
                    await using (scope.ConfigureAwait(false))
                    {
                        var removed = await scope.ServiceProvider.GetRequiredService<ICommandHandler<SweepWorktrees, int>>()
                            .Handle(new SweepWorktrees(), stoppingToken).ConfigureAwait(false);
                        if (removed is { IsSuccess: true, Value: > 0 })
                        {
                            LogRemoved(logger, removed.Value);
                        }
                    }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} unused checkout(s)")]
    private static partial void LogRemoved(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sweeping worktrees failed; retrying next interval")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
