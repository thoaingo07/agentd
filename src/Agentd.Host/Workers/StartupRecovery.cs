using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Application.Repositories;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Workers;

/// <summary>
/// Runs once before the polling and scheduling workers start: seeds repositories from configuration
/// and recovers jobs left over from the previous run. The resume runs are started by <see cref="SchedulerWorker"/>.
/// </summary>
internal sealed partial class StartupRecovery(
    IServiceScopeFactory scopes,
    IOptions<SchedulerOptions> options,
    ILogger<StartupRecovery> logger) : IHostedService
{
    public IReadOnlyList<AgentRunRequest> Resumes { get; private set; } = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var services = scope.ServiceProvider;
            try
            {
                var seeded = await services.GetRequiredService<ICommandHandler<SeedRepositories, SeedResult>>()
                    .Handle(new SeedRepositories(), cancellationToken).ConfigureAwait(false);
                foreach (var name in seeded.Value?.Seeded ?? [])
                {
                    LogSeeded(logger, name);
                }

                foreach (var error in seeded.Value?.Errors ?? [])
                {
                    LogSeedFailed(logger, error);
                }

                var plan = await services.GetRequiredService<ICommandHandler<RecoverJobsOnStartup, RecoveryPlan>>()
                    .Handle(new RecoverJobsOnStartup(), cancellationToken).ConfigureAwait(false);
                if (plan.IsSuccess)
                {
                    Resumes = plan.Value.Resume;
                    LogRecovered(logger, plan.Value.Resume.Count, plan.Value.RequeuedCount, plan.Value.RepublishedCount, plan.Value.FailedCount);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the host up (UI, health) even when the database or a remote is unreachable at startup.
                LogFailed(logger, ex);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Repository {Name} registered from configuration")]
    private static partial void LogSeeded(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configured repository not registered: {Error}")]
    private static partial void LogSeedFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Startup recovery: {Resumed} to resume, {Requeued} requeued, {Republished} republished, {Failed} failed")]
    private static partial void LogRecovered(ILogger logger, int resumed, int requeued, int republished, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Startup recovery failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
