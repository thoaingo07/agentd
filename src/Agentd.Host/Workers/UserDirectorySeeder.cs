using Agentd.Application.Abstractions;
using Agentd.Application.Users;

namespace Agentd.Host.Workers;

/// <summary>At startup, makes the user directory (the chat allowlist) match <c>Agentd:Users</c>.</summary>
internal sealed partial class UserDirectorySeeder(IServiceScopeFactory scopes, ILogger<UserDirectorySeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            try
            {
                var seeded = await scope.ServiceProvider.GetRequiredService<ICommandHandler<SeedUsers, int>>()
                    .Handle(new SeedUsers(), cancellationToken).ConfigureAwait(false);
                if (seeded.IsSuccess)
                {
                    LogSeeded(logger, seeded.Value);
                }
                else
                {
                    LogInvalid(logger, seeded.Error.Message);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the host up (UI, health); chat stays closed to everyone until the directory syncs.
                LogFailed(logger, ex);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "User directory synced from configuration ({Count} user(s))")]
    private static partial void LogSeeded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "User directory not synced: {Reason}")]
    private static partial void LogInvalid(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Syncing the user directory failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
