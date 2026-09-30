using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Mcp;

/// <summary>
/// Per-job bearer tokens for <c>/mcp</c>: 32 random bytes (base64url), stored only as SHA-256 hashes,
/// valid for a bounded time and revoked when the run ends. In memory: a restart issues fresh tokens.
/// </summary>
public sealed class McpTokenIssuer(IClock clock) : IMcpTokenIssuer
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, (long JobId, DateTimeOffset ExpiresAt)> _byHash = new(StringComparer.Ordinal);

    public string Issue(JobId jobId)
    {
        Revoke(jobId);   // one live token per job
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _byHash[Hash(token)] = (jobId.Value, clock.UtcNow + Lifetime);
        return token;
    }

    public JobId? Validate(string token)
    {
        if (string.IsNullOrEmpty(token) || !_byHash.TryGetValue(Hash(token), out var entry))
        {
            return null;
        }

        return entry.ExpiresAt > clock.UtcNow ? new JobId(entry.JobId) : null;
    }

    public void Revoke(JobId jobId)
    {
        foreach (var (hash, entry) in _byHash)
        {
            if (entry.JobId == jobId.Value)
            {
                _byHash.TryRemove(hash, out _);
            }
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
