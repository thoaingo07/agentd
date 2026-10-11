using Agentd.Application.Events;
using Agentd.Application.SystemLog;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Logging;

/// <summary>
/// Keeps the daemon's log for Settings → System log (<c>Agentd:SystemLog</c>): agentd's own categories from
/// <see cref="SystemLogOptions.MinimumLevel"/>, other libraries' from Warning. Messages are redacted like events, and a
/// <c>JobId</c> in the message's values links the entry to its job. The writer's and Npgsql's own entries are skipped,
/// so storing the log never logs itself.
/// </summary>
public sealed class SystemLogProvider(SystemLogWriter writer, IOptionsMonitor<SystemLogOptions> options) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SystemLogger(categoryName, writer, options);

    public void Dispose()
    {
    }

    internal static bool Skipped(string category) =>
        category.StartsWith("Agentd.Host.Logging", StringComparison.Ordinal) || category.StartsWith("Npgsql", StringComparison.Ordinal);

    private sealed class SystemLogger(string category, SystemLogWriter writer, IOptionsMonitor<SystemLogOptions> options) : ILogger
    {
        private readonly bool _own = category.StartsWith("Agentd", StringComparison.Ordinal);
        private readonly bool _skipped = Skipped(category);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            !_skipped && logLevel != LogLevel.None && options.CurrentValue is { Enabled: true } o && logLevel >= (_own ? o.MinimumLevel : LogLevel.Warning);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);
            writer.Write(new NewSystemLogEntry(DateTimeOffset.UtcNow, logLevel, category, SecretRedactor.Redact(formatter(state, exception)),
                exception is null ? null : SecretRedactor.Redact(exception.ToString()), JobOf(state)));
        }

        /// <summary>The job a message names in its values (<c>{JobId}</c>), if any.</summary>
        private static long? JobOf<TState>(TState state)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                return null;
            }

            foreach (var (key, value) in values)
            {
                if (string.Equals(key, "JobId", StringComparison.OrdinalIgnoreCase))
                {
                    return value switch
                    {
                        long l => l,
                        int i => i,
                        JobId j => j.Value,
                        _ => long.TryParse(value?.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
                    };
                }
            }

            return null;
        }
    }
}
