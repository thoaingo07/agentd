using Microsoft.Extensions.Logging;

namespace Agentd.Application.SystemLog;

/// <summary>One entry of the daemon's own log, to store.</summary>
public sealed record NewSystemLogEntry(DateTimeOffset At, LogLevel Level, string Category, string Message, string? Exception, long? JobId);

/// <summary>One stored entry (Settings → System log).</summary>
public sealed record SystemLogEntry(long Id, DateTimeOffset At, LogLevel Level, string Category, string Message, string? Exception, long? JobId);

/// <summary>The daemon's log via <c>agentd.system_log_*</c> routines.</summary>
public interface ISystemLogStore
{
    /// <summary>Stores a batch; returns how many were added.</summary>
    Task<int> AddAsync(IReadOnlyList<NewSystemLogEntry> entries, CancellationToken cancellationToken);

    /// <summary>Newest first, older than <paramref name="beforeId"/>, at <paramref name="minLevel"/> or above.</summary>
    Task<IReadOnlyList<SystemLogEntry>> ListAsync(long? beforeId, LogLevel minLevel, string? category, string? search, int limit, CancellationToken cancellationToken);

    /// <summary>Deletes entries logged before <paramref name="before"/>; returns how many.</summary>
    Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken);
}

/// <summary><c>Agentd:SystemLog</c>: what the daemon keeps of its own log, and for how long.</summary>
public sealed class SystemLogOptions
{
    public const string Section = "Agentd:SystemLog";

    /// <summary>Keep the log in the database at all (the console and journald keep working either way).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>agentd's own entries at this level and above are kept; other libraries' only from Warning.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>Days entries are kept (the hourly sweep deletes older ones); 0 keeps them forever.</summary>
    public int RetentionDays { get; set; } = 14;
}
