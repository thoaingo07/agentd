using System.Security.Cryptography;
using System.Text;
using Agentd.Application.Ports;
using Agentd.Domain.Common;

namespace Agentd.Application.AzureDevOps;

/// <summary>A started sign-in: where to send the browser, and what the callback must bring back (kept in a cookie).</summary>
public sealed record ConnectStart(Uri AuthorizeUrl, string State, string CodeVerifier);

/// <summary>
/// A person's connection as they see it (never the token): how they connected, and the name and email their commits
/// get (<paramref name="CommitName"/>/<paramref name="CommitEmail"/>: what they set; <paramref name="Author"/>: what's used, null when there's no email).
/// </summary>
public sealed record AdoConnectionView(Guid IdentityId, string UniqueName, string DisplayName, bool Failed, string? LastError, DateTimeOffset ConnectedAt,
    AdoConnectionKind Kind = AdoConnectionKind.OAuth, string? CommitName = null, string? CommitEmail = null, CommitAuthor? Author = null)
{
    internal static AdoConnectionView Of(AdoUserConnection c) =>
        new(c.IdentityId, c.UniqueName, c.DisplayName, c.Failed, c.LastError, c.ConnectedAt, c.Kind, c.CommitName, c.CommitEmail, c.Author);
}

/// <summary>
/// Connect / Disconnect a person's Azure DevOps (docs/architect/ado-user-delegation.md §2): the PKCE pair and the
/// one-time state, the redeemed refresh token encrypted before it's stored, and a person's own connections. Or a
/// personal access token (§2.1), checked with Azure DevOps and encrypted the same way.
/// </summary>
public sealed class AdoConnections(IAdoDelegation delegation, IAdoUserConnections connections, ITokenProtector protector, IAdoPatCheck? pats = null)
{
    private const int MaxField = 200;

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

        await connections.UpsertAsync(signIn.IdentityId, signIn.UniqueName, signIn.DisplayName, webLogin, protector.Protect(signIn.RefreshToken), AdoConnectionKind.OAuth, cancellationToken).ConfigureAwait(false);
        return new AdoConnectionView(signIn.IdentityId, signIn.UniqueName, signIn.DisplayName, false, null, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Connects with a personal access token: Azure DevOps says whose it is, then it's stored encrypted (replacing a
    /// Microsoft sign-in for the same person). The optional commit name and email are saved with it.
    /// </summary>
    public async Task<Result<AdoConnectionView>> AddPatAsync(string webLogin, string? pat, string? commitName, string? commitEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webLogin);
        var token = pat?.Trim();
        if (pats is null || string.IsNullOrEmpty(token) || token.Length > 1024 || token.Any(char.IsWhiteSpace))
        {
            return DomainError.Validation("Paste a personal access token from Azure DevOps (User settings → Personal access tokens).");
        }

        if (CheckAuthor(commitName, commitEmail) is { } invalid)
        {
            return invalid;
        }

        DelegatedSignIn who;
        try
        {
            who = await pats.WhoAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return DomainError.Validation(ex.Message);
        }

        await connections.UpsertAsync(who.IdentityId, who.UniqueName, who.DisplayName, webLogin, protector.Protect(token), AdoConnectionKind.Pat, cancellationToken).ConfigureAwait(false);
        await connections.SetCommitAuthorAsync(who.IdentityId, webLogin, Blank(commitName), Blank(commitEmail), cancellationToken).ConfigureAwait(false);
        var saved = await connections.FindByIdentityAsync(who.IdentityId, cancellationToken).ConfigureAwait(false);
        return saved is null ? DomainError.NotFound("The connection") : AdoConnectionView.Of(saved);
    }

    /// <summary>The name and email on the person's commits (blank: from their profile); only their own connection.</summary>
    public async Task<Result<AdoConnectionView>> SetCommitAuthorAsync(string webLogin, Guid identityId, string? name, string? email, CancellationToken cancellationToken)
    {
        if (CheckAuthor(name, email) is { } invalid)
        {
            return invalid;
        }

        if (!await connections.SetCommitAuthorAsync(identityId, webLogin, Blank(name), Blank(email), cancellationToken).ConfigureAwait(false)
            || await connections.FindByIdentityAsync(identityId, cancellationToken).ConfigureAwait(false) is not { } saved)
        {
            return DomainError.NotFound("Your connection");
        }

        return AdoConnectionView.Of(saved);
    }

    public async Task<IReadOnlyList<AdoConnectionView>> MineAsync(string webLogin, CancellationToken cancellationToken) =>
        [.. (await connections.ListByWebLoginAsync(webLogin, cancellationToken).ConfigureAwait(false)).Select(AdoConnectionView.Of)];

    /// <summary>Only the person's own; false when it isn't theirs (or is already gone).</summary>
    public Task<bool> DisconnectAsync(string webLogin, Guid identityId, CancellationToken cancellationToken) =>
        connections.DeleteAsync(identityId, webLogin, cancellationToken);

    private static DomainError? CheckAuthor(string? name, string? email)
    {
        if (name?.Length > MaxField || email?.Length > MaxField || (name ?? "").Any(char.IsControl) || (email ?? "").Any(char.IsControl))
        {
            return DomainError.Validation($"The commit name and email are at most {MaxField} characters, on one line.");
        }

        // git refuses '<' and '>' in either; an email needs its '@'.
        var e = Blank(email);
        return (name ?? "").IndexOfAny(['<', '>']) >= 0 || (e is not null && (e.IndexOfAny(['<', '>', ' ']) >= 0 || !e.Contains('@', StringComparison.Ordinal)))
            ? DomainError.Validation("That isn't a commit name and email git accepts (an email like you@example.com, no < or >).")
            : null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
