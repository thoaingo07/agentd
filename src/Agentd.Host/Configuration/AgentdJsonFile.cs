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

    public IReadOnlyList<string> Children(string section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (Find(Load(), section.Split(':')) is JsonObject node)
        {
            return [.. node.Select(p => p.Key)];
        }

        return configuration is null ? [] : [.. configuration.GetSection($"{Options.AgentdOptions.Section}:{section}").GetChildren().Select(c => c.Key)];
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
            node = node is null ? null : Child(node, segment);
        }

        return node;
    }

    /// <summary>Sets or removes one key. A numeric segment is an array index (<c>Users:0:Name</c>), like configuration.</summary>
    private static void Apply(JsonObject root, string[] path, string? value)
    {
        JsonNode current = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            var next = Child(current, path[i]);
            if (next is not (JsonObject or JsonArray))
            {
                if (value is null)
                {
                    return;   // nothing to remove
                }

                next = Index(path[i + 1]) is null ? new JsonObject() : new JsonArray();
                SetChild(current, path[i], next);
            }

            current = next;
        }

        if (value is null)
        {
            RemoveChild(current, path[^1]);
        }
        else
        {
            SetChild(current, path[^1], JsonValue.Create(value));
        }
    }

    private static JsonNode? Child(JsonNode node, string segment) => node switch
    {
        JsonObject o => Property(o, segment) is { } name ? o[name] : null,
        JsonArray a => Index(segment) is { } i && i < a.Count ? a[i] : null,
        _ => null,
    };

    private static void SetChild(JsonNode node, string segment, JsonNode value)
    {
        if (node is JsonObject o)
        {
            o[Property(o, segment) ?? segment] = value;
            return;
        }

        var array = (JsonArray)node;
        var index = Index(segment) ?? throw new ArgumentException($"'{segment}' isn't an array index.", nameof(segment));
        if (index < array.Count)
        {
            array[index] = value;
        }
        else if (index == array.Count)
        {
            array.Add(value);
        }
        else
        {
            throw new ArgumentException($"Index {index} would leave a gap (the array has {array.Count} items).", nameof(segment));
        }
    }

    private static void RemoveChild(JsonNode node, string segment)
    {
        if (node is JsonObject o && Property(o, segment) is { } name)
        {
            o.Remove(name);
        }
        else if (node is JsonArray a && Index(segment) is { } i && i < a.Count)
        {
            a.RemoveAt(i);
        }
    }

    private static int? Index(string segment) =>
        int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : null;

    private static string? Property(JsonObject o, string name) =>
        o.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
}
