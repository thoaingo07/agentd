using Agentd.Application.Setup;

namespace Agentd.Host;

/// <summary>
/// <see cref="IDaemonRestart"/> for <c>agentd daemon run</c>: stops the host a moment after the request, then
/// <see cref="DaemonHost"/> exits with <see cref="Cli.ExitCodes.Restart"/> (non-zero, so <c>Restart=on-failure</c> counts it too).
/// <c>Agentd:Daemon:Supervised</c> says a supervisor restarts agentd: the image and <c>agentd daemon install</c>'s unit set it.
/// </summary>
internal sealed partial class DaemonRestart(IConfiguration configuration, IHostApplicationLifetime lifetime, ISettingsAudit audit, TimeProvider time, ILogger<DaemonRestart> logger)
    : IDaemonRestart
{
    public const string SupervisedKey = "Agentd:Daemon:Supervised";

    /// <summary>How long the answer gets before the host stops.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    private int _requested;

    public bool Requested => Volatile.Read(ref _requested) == 1;

    public RestartAvailability Availability => configuration.GetValue<bool>(SupervisedKey)
        ? new(true, null)
        : new(false, "Nothing would start agentd again: run it in the container (restart: unless-stopped) or as the service from `agentd daemon install`, or set Agentd:Daemon:Supervised=true when something else restarts it.");

    public async Task<bool> RequestAsync(string by, CancellationToken cancellationToken)
    {
        if (!Availability.Available)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _requested, 1) == 1)
        {
            return true;   // already on its way
        }

        await audit.RecordAsync([new SettingsChange("Daemon", "daemon", "restart", by, time.GetUtcNow())], cancellationToken).ConfigureAwait(false);
        LogRestart(logger, by);
        _ = Task.Delay(Delay, time, CancellationToken.None).ContinueWith(_ => lifetime.StopApplication(), TaskScheduler.Default);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restart requested from Settings by {By}: stopping; the supervisor starts agentd again")]
    private static partial void LogRestart(ILogger logger, string by);
}
