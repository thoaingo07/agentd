using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace Agentd.Host.Configuration;

/// <summary>A stored secret's metadata; the value is never listed.</summary>
internal sealed record SecretInfo(string Key, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>
/// <c>~/.agentd/config/secrets.json</c>: values encrypted with ASP.NET Core Data Protection (the key ring in
/// <c>~/.agentd/keys</c>, shared with the daemon's cookies), written atomically with mode 0600
/// (docs/architect/deployment.md §4). Loaded as configuration; never put into an agent's environment.
/// </summary>
internal sealed partial class SecretStore(ConfigHome home, TimeProvider? time = null)
{
    public const string Purpose = "agentd.secrets.v1";
    public const int MaxValueLength = 64 * 1024;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IDataProtector? _protector;

    public string FilePath => Path.Combine(Path.GetDirectoryName(home.ConfigFile)!, "secrets.json");

    /// <summary>Letters, digits and <c>: _ - .</c>, up to 200 characters (a configuration path such as <c>AzureDevOps:Pat</c>).</summary>
    public static bool IsValidKey(string? key) => key is { Length: > 0 and <= 200 } && KeyPattern().IsMatch(key);

    /// <summary>The configuration path a secret fills: <c>Agentd:&lt;key&gt;</c>, or the key itself for <c>ConnectionStrings:*</c>.</summary>
    public static string ConfigKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase) ? key : "Agentd:" + key;
    }

    public IReadOnlyList<SecretInfo> List() =>
        Read().Secrets.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new SecretInfo(s.Key, s.Value.UpdatedAt, s.Value.UpdatedBy)).ToList();

    public void Set(string key, string value, string by)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsValidKey(key))
        {
            throw new ArgumentException($"Invalid secret name '{key}': use letters, digits and ':', '_', '-', '.' (at most 200).", nameof(key));
        }

        if (value.Length is 0 or > MaxValueLength)
        {
            throw new ArgumentException($"A secret value is 1 to {MaxValueLength} characters.", nameof(value));
        }

        Update(file => file.Secrets[key] = new Entry(Protector.Protect(value), _time.GetUtcNow(), by));
    }

    public bool Remove(string key)
    {
        var removed = false;
        Update(file => removed = file.Secrets.Remove(key));
        return removed;
    }

    /// <summary>Every secret that decrypts, and the names of those that don't (e.g. the key ring was replaced).</summary>
    public (IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Unreadable) Load()
    {
        var file = Read();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();
        foreach (var (key, entry) in file.Secrets)
        {
            try
            {
                values[key] = Protector.Unprotect(entry.Value);
            }
            catch (CryptographicException)
            {
                unreadable.Add(key);
            }
        }

        return (values, unreadable);
    }

    private IDataProtector Protector => _protector ??= DataProtectionProvider
        .Create(new DirectoryInfo(home.Keys), b => b.SetApplicationName(ConfigHome.ApplicationName))
        .CreateProtector(Purpose);

    private SecretsFile Read()
    {
        if (!File.Exists(FilePath))
        {
            return new SecretsFile();
        }

        var file = JsonSerializer.Deserialize<SecretsFile>(File.ReadAllText(FilePath), s_json) ?? new SecretsFile();
        file.Secrets = new Dictionary<string, Entry>(file.Secrets, StringComparer.Ordinal);
        return file;
    }

    /// <summary>Read-modify-write under an exclusive lock file, then an atomic rename of a 0600 temp file.</summary>
    private void Update(Action<SecretsFile> change)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        using var _ = new FileStream(Path.Combine(dir, "secrets.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var file = Read();
        change(file);
        var temp = FilePath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(stream, file, s_json);
        }

        File.Move(temp, FilePath, overwrite: true);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+(:[A-Za-z0-9_.\-]+)*$")]
    private static partial Regex KeyPattern();

    private sealed class SecretsFile
    {
        public Dictionary<string, Entry> Secrets { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record Entry(string Value, DateTimeOffset UpdatedAt, string UpdatedBy);
}
