using System.Collections.Concurrent;
using Agentd.Application.Ports;
using Agentd.Domain.Common;

namespace Agentd.Application.AzureDevOps;

/// <summary>
/// Access tokens for Azure DevOps as a person: from their stored refresh token (rotated and stored again), cached in
/// memory until five minutes before expiry, one refresh at a time per person. A refused refresh marks the connection
/// Failed (they reconnect); an unreachable Entra only skips this time.
/// </summary>
public sealed class AdoUserTokens(IAdoUserConnections connections, ITokenProtector protector, IAdoDelegation delegation, IClock clock)
{
    private static readonly TimeSpan s_margin = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Guid, (string Token, DateTimeOffset ExpiresAt)> _cache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>A token for <paramref name="identityId"/>, or null when there's no working sign-in for them.</summary>
    public async Task<string?> GetAccessTokenAsync(Guid identityId, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && Cached(identityId) is { } cached)
        {
            return cached;
        }

        var gate = _locks.GetOrAdd(identityId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && Cached(identityId) is { } fresh)
            {
                return fresh;   // another caller refreshed while this one waited
            }

            _cache.TryRemove(identityId, out _);
            if (await connections.FindByIdentityAsync(identityId, cancellationToken).ConfigureAwait(false) is not { Failed: false } connection)
            {
                return null;
            }

            if (protector.Unprotect(connection.RefreshToken) is not { } refresh)
            {
                await connections.MarkFailedAsync(identityId, "The stored sign-in can't be read (agentd's keys changed). Connect again.", cancellationToken).ConfigureAwait(false);
                return null;
            }

            DelegatedToken token;
            try
            {
                token = await delegation.RefreshAsync(refresh, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                await connections.MarkFailedAsync(identityId, ex.Message, cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (HttpRequestException)
            {
                return null;   // Entra unreachable: agentd's identity this time, the sign-in stays
            }

            if (token.RefreshToken is { Length: > 0 } rotated)
            {
                await connections.StoreTokenAsync(identityId, protector.Protect(rotated), cancellationToken).ConfigureAwait(false);
            }

            _cache[identityId] = (token.AccessToken, clock.UtcNow + token.ExpiresIn);
            return token.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private string? Cached(Guid identityId) =>
        _cache.TryGetValue(identityId, out var c) && c.ExpiresAt - s_margin > clock.UtcNow ? c.Token : null;
}
