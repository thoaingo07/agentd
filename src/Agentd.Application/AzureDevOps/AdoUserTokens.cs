using System.Collections.Concurrent;
using Agentd.Application.Ports;
using Agentd.Domain.Common;

namespace Agentd.Application.AzureDevOps;

/// <summary>
/// Access tokens for Azure DevOps as a person: from their stored refresh token (rotated and stored again), cached in
/// memory until five minutes before expiry, one refresh at a time per person. A refused refresh marks the connection
/// Failed (they reconnect); an unreachable Entra only skips this time. A person who connected with a personal access
/// token gets it back as is (<see cref="AdoUserCredential.IsPat"/>: Basic auth, not Bearer).
/// </summary>
public sealed class AdoUserTokens(IAdoUserConnections connections, ITokenProtector protector, IAdoDelegation delegation, IClock clock)
{
    private static readonly TimeSpan s_margin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_patCache = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, (AdoUserCredential Credential, DateTimeOffset ExpiresAt)> _cache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>A token for <paramref name="identityId"/>, or null when there's no working sign-in for them.</summary>
    public async Task<string?> GetAccessTokenAsync(Guid identityId, bool forceRefresh, CancellationToken cancellationToken) =>
        (await GetCredentialAsync(identityId, forceRefresh, cancellationToken).ConfigureAwait(false))?.Token;

    /// <summary>The bearer token or PAT for <paramref name="identityId"/>, or null when there's no working sign-in for them.</summary>
    public async Task<AdoUserCredential?> GetCredentialAsync(Guid identityId, bool forceRefresh, CancellationToken cancellationToken)
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

            if (connection.Kind == AdoConnectionKind.Pat)
            {
                var pat = new AdoUserCredential(refresh, IsPat: true);
                _cache[identityId] = (pat, clock.UtcNow + s_patCache);
                return pat;
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

            var bearer = new AdoUserCredential(token.AccessToken, IsPat: false);
            _cache[identityId] = (bearer, clock.UtcNow + token.ExpiresIn);
            return bearer;
        }
        finally
        {
            gate.Release();
        }
    }

    private AdoUserCredential? Cached(Guid identityId) =>
        _cache.TryGetValue(identityId, out var c) && c.ExpiresAt - s_margin > clock.UtcNow ? c.Credential : null;
}

/// <summary>What authenticates as a person: an Entra access token (Bearer), or their personal access token (Basic).</summary>
public sealed record AdoUserCredential(string Token, bool IsPat)
{
    /// <summary>Never print the token.</summary>
    public override string ToString() => $"AdoUserCredential {{ IsPat = {IsPat} }}";
}
