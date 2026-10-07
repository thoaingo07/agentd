using System.Text.Json;
using System.Text.Json.Nodes;
using Agentd.Application.Setup;

namespace Agentd.Host.Configuration;

/// <summary>
/// Writes <c>~/.agentd/config/agentd.json</c> for the setup wizard, Settings and <c>agentd init</c>: read-modify-write
/// under a lock file, then an atomic rename of a 0600 temp file. Keys are matched case-insensitively, like
/// configuration; comments in the file don't survive a write.
/// </summary>
internal sealed class AgentdJsonFile(ConfigHome home, IConfiguration? configuration = null) : IConfigWriter, IDisposable
{
    private static readonly JsonDocumentOptions s_read = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string? Read(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var node = Find(Load(), key.Split(':'));
        if (node is JsonValue value)
        {
            return value.TryGetValue<string>(out var text) ? text : value.ToJsonString();
        }

        return configuration?[$"{Options.AgentdOptions.Section}:{key}"];
    }

    public async Task SetAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(home.ConfigFile)!;
            Directory.CreateDirectory(dir);
            using var _ = new FileStream(Path.Combine(dir, "agentd.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var root = Load() ?? new JsonObject();
            foreach (var (key, value) in values)
            {
                Apply(root, key.Split(':'), value);
            }

            var temp = $"{home.ConfigFile}.{Guid.NewGuid():N}.tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temp, options))
            {
                await JsonSerializer.SerializeAsync(stream, root, s_write, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, home.ConfigFile, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    private JsonObject? Load() =>
        File.Exists(home.ConfigFile) ? JsonNode.Parse(File.ReadAllText(home.ConfigFile), documentOptions: s_read) as JsonObject : null;

    private static JsonNode? Find(JsonNode? node, string[] path)
    {
        foreach (var segment in path)
        {
            node = node is JsonObject o && Property(o, segment) is { } name ? o[name] : null;
        }

        return node;
    }

    private static void Apply(JsonObject root, string[] path, string? value)
    {
        var current = root;
        foreach (var segment in path[..^1])
        {
            var name = Property(current, segment);
            if (name is null || current[name] is not JsonObject child)
            {
                if (value is null)
                {
                    return;   // nothing to remove
                }

                child = [];
                current[name ?? segment] = child;
            }

            current = child;
        }

        var leaf = Property(current, path[^1]) ?? path[^1];
        if (value is null)
        {
            current.Remove(leaf);
        }
        else
        {
            current[leaf] = value;
        }
    }

    private static string? Property(JsonObject o, string name) =>
        o.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
}
