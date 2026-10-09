using System.Security.Cryptography;
using System.Text;
using Agentd.Application.Ports;
using Agentd.Domain.Common;

namespace Agentd.Application.AzureDevOps;

/// <summary>A started sign-in: where to send the browser, and what the callback must bring back (kept in a cookie).</summary>
public sealed record ConnectStart(Uri AuthorizeUrl, string State, string CodeVerifier);

/// <summary>A person's connection as they see it (never the token).</summary>
public sealed record AdoConnectionView(Guid IdentityId, string UniqueName, string DisplayName, bool Failed, string? LastError, DateTimeOffset ConnectedAt);

/// <summary>
/// Connect / Disconnect a person's Azure DevOps (docs/architect/ado-user-delegation.md §2): the PKCE pair and the
/// one-time state, the redeemed refresh token encrypted before it's stored, and a person's own connections.
/// </summary>
public sealed class AdoConnections(IAdoDelegation delegation, IAdoUserConnections connections, ITokenProtector protector)
{
    public bool IsConfigured => delegation.IsConfigured;

    public ConnectStart Start(Uri redirectUri)
    {
        var state = Random();
        var verifier = Random();
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new ConnectStart(delegation.AuthorizeUrl(state, challenge, redirectUri), state, verifier);
    }

    /// <param name="webLogin">Who is signed in to agentd (they own the connection).</param>
    /// <param name="code">The code from the callback.</param>
    /// <param name="state">The state from the callback.</param>
    /// <param name="started">The state and PKCE verifier this browser started with (from its cookie); null when it has none.</param>
    /// <param name="redirectUri">The same callback URL the sign-in started with.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    public async Task<Result<AdoConnectionView>> CompleteAsync(string webLogin, string? code, string? state, (string State, string CodeVerifier)? started, Uri redirectUri, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webLogin);
        if (started is not { } begun || string.IsNullOrEmpty(state)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state), Encoding.ASCII.GetBytes(begun.State)))
        {
            return DomainError.Validation("This sign-in didn't start here, or it expired. Click Connect again.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return DomainError.Validation("Microsoft didn't send a sign-in code. Click Connect again.");
        }

        DelegatedSignIn signIn;
        try
        {
            signIn = await delegation.RedeemAsync(code, begun.CodeVerifier, redirectUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return DomainError.Validation($"The sign-in didn't work: {ex.Message}");
        }

        await connections.UpsertAsync(signIn.IdentityId, signIn.UniqueName, signIn.DisplayName, webLogin, protector.Protect(signIn.RefreshToken), cancellationToken).ConfigureAwait(false);
        return new AdoConnectionView(signIn.IdentityId, signIn.UniqueName, signIn.DisplayName, false, null, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<AdoConnectionView>> MineAsync(string webLogin, CancellationToken cancellationToken) =>
        [.. (await connections.ListByWebLoginAsync(webLogin, cancellationToken).ConfigureAwait(false))
            .Select(c => new AdoConnectionView(c.IdentityId, c.UniqueName, c.DisplayName, c.Failed, c.LastError, c.ConnectedAt))];

    /// <summary>Only the person's own; false when it isn't theirs (or is already gone).</summary>
    public Task<bool> DisconnectAsync(string webLogin, Guid identityId, CancellationToken cancellationToken) =>
        connections.DeleteAsync(identityId, webLogin, cancellationToken);

    private static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
