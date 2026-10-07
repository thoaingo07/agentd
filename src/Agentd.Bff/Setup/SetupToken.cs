using System.Security.Cryptography;
using System.Text;
using Agentd.Application.Setup;

namespace Agentd.Bff.Setup;

/// <summary>
/// The one-time setup link's token: 32 random bytes (base64url). Only its SHA-256 is stored, in
/// <c>~/.agentd/run/setup-token</c> (0600), so the file alone can't open a setup session. Issuing a new token
/// replaces the old one (the daemon prints a fresh link on every start, <c>agentd setup-link</c> on request).
/// </summary>
public sealed class SetupToken(string path) : ISetupLink
{
    public string FilePath { get; } = path;

    public string Issue()
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = $"{FilePath}.{Guid.NewGuid():N}.tmp";   // one per writer: the daemon and `agentd setup-link` may race
        File.WriteAllText(temp, Hash(token));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, FilePath, overwrite: true);
        return token;
    }

    /// <summary>True when <paramref name="token"/> is the current one (constant-time compare).</summary>
    public bool Verify(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return false;
        }

        string stored;
        try
        {
            stored = File.ReadAllText(FilePath).Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(stored));
    }

    /// <summary>Kills the link (setup is complete).</summary>
    public void Revoke() => File.Delete(FilePath);   // a missing file is fine: already revoked

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
