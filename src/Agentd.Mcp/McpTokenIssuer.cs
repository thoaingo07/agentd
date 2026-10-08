using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Mcp;

/// <summary>
/// Bearer tokens for <c>/mcp</c>, per job (the job tools) or per chat (the read-only chat tools): 32 random bytes
/// (base64url), stored only as SHA-256 hashes, valid for a bounded time and revoked when the run or chat ends. In
/// memory: a restart issues fresh tokens.
/// </summary>
public sealed class McpTokenIssuer(IClock clock) : IMcpTokenIssuer
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private const string Job = "job";
    private const string Chat = "chat";

    private readonly ConcurrentDictionary<string, (string Kind, long Id, DateTimeOffset ExpiresAt)> _byHash = new(StringComparer.Ordinal);

    public string Issue(JobId jobId) => IssueFor(Job, jobId.Value);

    public JobId? Validate(string token) => Find(Job, token) is { } id ? new JobId(id) : null;

    public void Revoke(JobId jobId) => RevokeFor(Job, jobId.Value);

    public string IssueChat(long chatId) => IssueFor(Chat, chatId);

    public long? ValidateChat(string token) => Find(Chat, token);

    public void RevokeChat(long chatId) => RevokeFor(Chat, chatId);

    private string IssueFor(string kind, long id)
    {
        RevokeFor(kind, id);   // one live token per job or chat
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _byHash[Hash(token)] = (kind, id, clock.UtcNow + Lifetime);
        return token;
    }

    private long? Find(string kind, string token) =>
        !string.IsNullOrEmpty(token) && _byHash.TryGetValue(Hash(token), out var entry) && entry.Kind == kind && entry.ExpiresAt > clock.UtcNow ? entry.Id : null;

    private void RevokeFor(string kind, long id)
    {
        foreach (var (hash, entry) in _byHash)
        {
            if (entry.Kind == kind && entry.Id == id)
            {
                _byHash.TryRemove(hash, out _);
            }
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
