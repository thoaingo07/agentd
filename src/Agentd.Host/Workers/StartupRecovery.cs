using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Application.Repositories;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Workers;

/// <summary>
/// Runs once before the polling and scheduling workers start: seeds repositories from configuration
/// and recovers jobs left over from the previous run (the resume runs are started by <see cref="SchedulerWorker"/>), then
/// picks up ideas the agent never answered.
/// </summary>
internal sealed partial class StartupRecovery(
    IServiceScopeFactory scopes,
    IOptions<SchedulerOptions> options,
    ILogger<StartupRecovery> logger) : IHostedService
{
    public IReadOnlyList<AgentRunRequest> Resumes { get; private set; } = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await DropDisabledChatsAsync(cancellationToken).ConfigureAwait(false);
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

                // Ideas whose last messages were never answered (a failed or cut-off turn) get them again.
                if (services.GetService<Application.Ideas.IdeaService>() is { } ideas)
                {
                    await ideas.ResumeAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>A chat switched off since the last run: what's queued for it is marked failed and its threads closed.</summary>
    private async Task DropDisabledChatsAsync(CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            try
            {
                var enabled = scope.ServiceProvider.GetRequiredService<Application.Messaging.IMessagingProviderRegistry>().Enabled.Select(p => p.Key).ToList();
                var dropped = await scope.ServiceProvider.GetRequiredService<Application.Ports.IConversationStore>().DropDisabledAsync(enabled, cancellationToken).ConfigureAwait(false);
                if (dropped.Failed + dropped.Closed > 0)
                {
                    LogDropped(logger, dropped.Failed, dropped.Closed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Chats switched off: {Failed} queued message(s) marked failed, {Closed} thread(s) closed")]
    private static partial void LogDropped(ILogger logger, int failed, int closed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Repository {Name} registered from configuration")]
    private static partial void LogSeeded(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configured repository not registered: {Error}")]
    private static partial void LogSeedFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Startup recovery: {Resumed} to resume, {Requeued} requeued, {Republished} republished, {Failed} failed")]
    private static partial void LogRecovered(ILogger logger, int resumed, int requeued, int republished, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Startup recovery failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
