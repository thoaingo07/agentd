namespace Agentd.Application.Ports;

/// <summary>
/// A person's delegated Azure DevOps sign-in (docs/architect/ado-user-delegation.md). <see cref="RefreshToken"/> is
/// encrypted (<see cref="ITokenProtector"/>); nothing here is ever shown to anyone.
/// </summary>
/// <param name="IdentityId">Their Azure DevOps identity id (what a work item's Assigned To carries).</param>
/// <param name="UniqueName">Their sign-in name (email/UPN).</param>
/// <param name="DisplayName">Their name.</param>
/// <param name="WebLogin">The agentd web login that connected it.</param>
/// <param name="RefreshToken">The encrypted refresh token.</param>
/// <param name="Failed">A refresh was refused (revoked, expired): <paramref name="LastError"/> says why.</param>
/// <param name="LastError">Why the last refresh failed.</param>
/// <param name="ConnectedAt">When they connected.</param>
/// <param name="RefreshedAt">When the token was last renewed.</param>
public sealed record AdoUserConnection(Guid IdentityId, string UniqueName, string DisplayName, string WebLogin, byte[] RefreshToken,
    bool Failed, string? LastError, DateTimeOffset ConnectedAt, DateTimeOffset RefreshedAt);

/// <summary>The delegated sign-ins, via <c>agentd.ado_connection_*</c> routines.</summary>
public interface IAdoUserConnections
{
    /// <summary>Connects (or reconnects) a person: replaces their token and clears a failure.</summary>
    Task UpsertAsync(Guid identityId, string uniqueName, string displayName, string webLogin, byte[] refreshToken, CancellationToken cancellationToken);

    Task<AdoUserConnection?> FindByIdentityAsync(Guid identityId, CancellationToken cancellationToken);

    /// <summary>By sign-in name, case-insensitively (a <c>!review</c> requester's email).</summary>
    Task<AdoUserConnection?> FindByUniqueNameAsync(string uniqueName, CancellationToken cancellationToken);

    Task<IReadOnlyList<AdoUserConnection>> ListByWebLoginAsync(string webLogin, CancellationToken cancellationToken);

    /// <summary>Stores a rotated refresh token; false when the connection is gone.</summary>
    Task<bool> StoreTokenAsync(Guid identityId, byte[] refreshToken, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid identityId, string reason, CancellationToken cancellationToken);

    /// <summary>Removes it, only for the web login that connected it; false otherwise.</summary>
    Task<bool> DeleteAsync(Guid identityId, string webLogin, CancellationToken cancellationToken);
}

/// <summary>Encrypts tokens at rest (ASP.NET Core Data Protection in the daemon).</summary>
public interface ITokenProtector
{
    byte[] Protect(string token);

    /// <summary>The token, or null when it can't be decrypted (the keys changed): the person reconnects.</summary>
    string? Unprotect(byte[] protectedToken);
}
