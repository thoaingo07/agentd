namespace Agentd.Application.Setup;

/// <summary>Whether Settings may restart agentd, and why not (no supervisor would start it again).</summary>
public sealed record RestartAvailability(bool Available, string? Reason);

/// <summary>
/// Settings → Restart now: agentd stops (running agents end like on <c>docker stop</c>) and exits with
/// <c>ExitCodes.Restart</c>, and its supervisor (a container restart policy, systemd's <c>Restart=on-failure</c>) starts it
/// again; jobs resume where they were. Without a supervisor (<c>Agentd:Daemon:Supervised</c>) nothing would start it, so
/// it isn't offered.
/// </summary>
public interface IDaemonRestart
{
    RestartAvailability Availability { get; }

    /// <summary>Stops agentd shortly (so the answer gets out); false when it isn't available.</summary>
    Task<bool> RequestAsync(string by, CancellationToken cancellationToken);
}
