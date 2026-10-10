namespace Agentd.Application.Ports;

/// <summary>
/// A person's delegated Azure DevOps sign-in (docs/architect/ado-user-delegation.md). <see cref="RefreshToken"/> is
/// encrypted (<see cref="ITokenProtector"/>); nothing here is ever shown to anyone.
/// </summary>
/// <param name="IdentityId">Their Azure DevOps identity id (what a work item's Assigned To carries).</param>
/// <param name="UniqueName">Their sign-in name (email/UPN).</param>
/// <param name="DisplayName">Their name.</param>
/// <param name="WebLogin">The agentd web login that connected it.</param>
/// <param name="RefreshToken">The encrypted secret: a refresh token, or a personal access token (<paramref name="Kind"/>).</param>
/// <param name="Failed">A refresh was refused (revoked, expired): <paramref name="LastError"/> says why.</param>
/// <param name="LastError">Why the last refresh failed.</param>
/// <param name="ConnectedAt">When they connected.</param>
/// <param name="RefreshedAt">When the token was last renewed.</param>
/// <param name="Kind">Microsoft's sign-in, or a personal access token.</param>
/// <param name="CommitName">The name on their commits; null: <paramref name="DisplayName"/>.</param>
/// <param name="CommitEmail">The email on their commits; null: <paramref name="UniqueName"/> when it's an email.</param>
public sealed record AdoUserConnection(Guid IdentityId, string UniqueName, string DisplayName, string WebLogin, byte[] RefreshToken,
    bool Failed, string? LastError, DateTimeOffset ConnectedAt, DateTimeOffset RefreshedAt,
    AdoConnectionKind Kind = AdoConnectionKind.OAuth, string? CommitName = null, string? CommitEmail = null)
{
    /// <summary>
    /// Who their commits are by: the name and email they set, else their Azure DevOps profile's. Null when there's no
    /// email to use (e.g. a sign-in name that isn't one): they set it in Settings → Your Azure DevOps.
    /// </summary>
    public CommitAuthor? Author
    {
        get
        {
            // A Microsoft account invited as a guest signs in as "live.com#someone@example.com".
            var profile = UniqueName[(UniqueName.LastIndexOf('#') + 1)..];
            var email = CommitEmail ?? (profile.Contains('@', StringComparison.Ordinal) ? profile : null);
            return email is null ? null : new CommitAuthor(CommitName ?? DisplayName, email);
        }
    }
}

/// <summary>How a person connected their Azure DevOps.</summary>
public enum AdoConnectionKind
{
    /// <summary>Microsoft Entra's sign-in (a refresh token).</summary>
    OAuth,

    /// <summary>A personal access token they pasted.</summary>
    Pat,
}

/// <summary>The delegated sign-ins, via <c>agentd.ado_connection_*</c> routines.</summary>
public interface IAdoUserConnections
{
    /// <summary>Connects (or reconnects) a person: replaces their secret and clears a failure; their commit name and email stay.</summary>
    Task UpsertAsync(Guid identityId, string uniqueName, string displayName, string webLogin, byte[] refreshToken, AdoConnectionKind kind, CancellationToken cancellationToken);

    /// <summary>Their commit name and email (null: from their profile), only for the web login that connected it; false otherwise.</summary>
    Task<bool> SetCommitAuthorAsync(Guid identityId, string webLogin, string? name, string? email, CancellationToken cancellationToken);

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

/// <summary>A completed delegated sign-in: who it is in Azure DevOps, and the refresh token (in clear, to be protected).</summary>
public sealed record DelegatedSignIn(Guid IdentityId, string UniqueName, string DisplayName, string RefreshToken)
{
    /// <summary>Never print the token (records print every property).</summary>
    public override string ToString() => $"DelegatedSignIn {{ IdentityId = {IdentityId}, UniqueName = {UniqueName} }}";
}

/// <summary>The Microsoft Entra sign-in that lets agentd act as a person in Azure DevOps (authorization code + PKCE).</summary>
public interface IAdoDelegation
{
    /// <summary>The app (tenant, client id, client secret) is set up, so people can connect.</summary>
    bool IsConfigured { get; }

    /// <summary>Where to send the browser: Entra's sign-in and consent page.</summary>
    Uri AuthorizeUrl(string state, string codeChallenge, Uri redirectUri);

    /// <summary>Redeems the code and asks Azure DevOps who signed in. Throws with Entra's or Azure DevOps' reason.</summary>
    Task<DelegatedSignIn> RedeemAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken cancellationToken);

    /// <summary>
    /// A fresh access token from a refresh token. Throws <see cref="InvalidOperationException"/> with Entra's reason when
    /// the sign-in is refused (revoked, expired), and <see cref="HttpRequestException"/> when Entra can't be reached.
    /// </summary>
    Task<DelegatedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}

/// <summary>Checks a personal access token against the organization (<c>connectionData</c>) and says whose it is.</summary>
public interface IAdoPatCheck
{
    /// <summary>Who the token belongs to (its <see cref="DelegatedSignIn.RefreshToken"/> is the PAT). Throws <see cref="InvalidOperationException"/> with why it was refused.</summary>
    Task<DelegatedSignIn> WhoAsync(string pat, CancellationToken cancellationToken);
}

/// <summary>An access token for Azure DevOps as a person, and the rotated refresh token when Entra sent one.</summary>
public sealed record DelegatedToken(string AccessToken, string? RefreshToken, TimeSpan ExpiresIn)
{
    /// <summary>Never print the tokens.</summary>
    public override string ToString() => $"DelegatedToken {{ ExpiresIn = {ExpiresIn} }}";
}
