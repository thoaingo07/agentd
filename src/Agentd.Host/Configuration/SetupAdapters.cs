using System.Text.Json;
using Agentd.Application.Setup;

namespace Agentd.Host.Configuration;

/// <summary><see cref="ISecrets"/> over the encrypted <see cref="SecretStore"/>.</summary>
internal sealed class StoredSecrets(SecretStore store) : ISecrets
{
    private readonly Lock _lock = new();

    public SecretStatus Status(string key) =>
        store.List().FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal)) is { } s
            ? new SecretStatus(true, s.UpdatedAt, s.UpdatedBy)
            : SecretStatus.Missing;

    public string? TryGet(string key) => store.Load().Values.TryGetValue(key, out var value) ? value : null;

    public void Store(string key, string value, string by)
    {
        lock (_lock)
        {
            store.Set(key, value, by);
        }
    }

    public bool Remove(string key)
    {
        lock (_lock)
        {
            return store.Remove(key);
        }
    }
}

/// <summary>
/// The <c>settings.changed</c> trail: one JSON line per change in <c>~/.agentd/logs/settings-audit.jsonl</c> (0600),
/// plus a log line. A file, because setup runs before the database exists.
/// </summary>
internal sealed partial class FileSettingsAudit(ConfigHome home, ILogger<FileSettingsAudit> logger) : ISettingsAudit, IDisposable
{
    public const string FileName = "settings-audit.jsonl";

    private static readonly JsonSerializerOptions s_json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string FilePath => Path.Combine(home.Logs, FileName);

    public async Task RecordAsync(IReadOnlyList<SettingsChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(home.Logs);
            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using var stream = new FileStream(FilePath, options);
            await using var writer = new StreamWriter(stream);
            foreach (var change in changes)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { @event = "settings.changed", change.Key, change.Kind, change.Action, change.By, change.At }, s_json)).ConfigureAwait(false);
                LogChange(logger, change.Key, change.Action, change.By);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings changed: {Key} {Action} by {By}")]
    private static partial void LogChange(ILogger logger, string key, string action, string by);
}
