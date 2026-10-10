using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Agentd.Application.Ports;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.AzureDevOps;

/// <summary>
/// The delegated sign-in over Microsoft Entra's v2.0 endpoints (docs/architect/ado-user-delegation.md §2), with the
/// service principal's app (<c>TenantId</c>, <c>ClientId</c>, <c>ClientSecret</c>) as a confidential client. Azure DevOps'
/// <c>connectionData</c> tells who signed in.
/// </summary>
public sealed class AdoDelegation(HttpClient http, IOptions<AzureDevOpsOptions> options) : IAdoDelegation
{
    /// <summary>Azure DevOps on a person's behalf, and a refresh token to keep doing it.</summary>
    public const string Scopes = "499b84ac-1321-427f-aa17-267ca6975798/user_impersonation offline_access";

    public static readonly Uri Authority = new("https://login.microsoftonline.com/");

    private AzureDevOpsOptions O => options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(O.TenantId) && !string.IsNullOrWhiteSpace(O.ClientId) && !string.IsNullOrWhiteSpace(O.ClientSecret);

    public Uri AuthorizeUrl(string state, string codeChallenge, Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        var query = string.Join('&', new Dictionary<string, string>
        {
            ["client_id"] = O.ClientId!.Trim(),
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.ToString(),
            ["response_mode"] = "query",
            ["scope"] = Scopes,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return new Uri(Authority, $"{Uri.EscapeDataString(O.TenantId!.Trim())}/oauth2/v2.0/authorize?{query}");
    }

    public async Task<DelegatedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, cancellationToken).ConfigureAwait(false);
        return new DelegatedToken(token["access_token"]!.GetValue<string>(), token["refresh_token"]?.GetValue<string>(),
            TimeSpan.FromSeconds(token["expires_in"]?.GetValue<int>() ?? 3600));
    }

    public async Task<DelegatedSignIn> RedeemAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        var token = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri.ToString(),
            ["code_verifier"] = codeVerifier,
        }, cancellationToken).ConfigureAwait(false);
        var access = token["access_token"]!.GetValue<string>();
        var refresh = token["refresh_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Microsoft Entra didn't return a refresh token (the app needs offline_access).");

        return await WhoAsync(http, O, new AuthenticationHeaderValue("Bearer", access), refresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Who <paramref name="auth"/> is in the organization (<c>connectionData</c>), carrying <paramref name="secret"/> along.</summary>
    internal static async Task<DelegatedSignIn> WhoAsync(HttpClient http, AzureDevOpsOptions o, AuthenticationHeaderValue auth, string secret, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(o.BaseUrl, $"{Uri.EscapeDataString(o.Organization)}/_apis/connectionData"));
        request.Headers.Authorization = auth;
        request.Headers.TryAddWithoutValidation("X-TFS-FedAuthRedirect", "Suppress");
        using var me = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var user = me.IsSuccessStatusCode && me.Content.Headers.ContentType?.MediaType == "application/json"
            ? JsonNode.Parse(await me.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))?["authenticatedUser"]
            : null;
        var name = user?["providerDisplayName"]?.GetValue<string>() ?? user?["customDisplayName"]?.GetValue<string>() ?? "";
        var unique = user?["properties"]?["Account"]?["$value"]?.GetValue<string>() ?? name;
        // A token Azure DevOps doesn't know can be answered as the anonymous identity rather than with a 401.
        if (user?["id"]?.GetValue<string>() is not { } id || !Guid.TryParse(id, out var identity) || identity == Guid.Empty
            || unique.Length == 0 || name == "Anonymous" || unique == "Anonymous")
        {
            throw new InvalidOperationException($"Azure DevOps ({o.Organization}) didn't accept this account. Is it a member of the organization?");
        }

        return new DelegatedSignIn(identity, unique, name.Length > 0 ? name : unique, secret);
    }

    /// <summary>The token endpoint as the app (a confidential client). Entra's refusal becomes its reason, never the request.</summary>
    private async Task<JsonNode> TokenAsync(Dictionary<string, string> grant, CancellationToken cancellationToken)
    {
        grant["client_id"] = O.ClientId!.Trim();
        grant["client_secret"] = O.ClientSecret!.Trim();
        grant["scope"] = Scopes;
        using var form = new FormUrlEncodedContent(grant);
        using var response = await http.PostAsync(new Uri(Authority, $"{Uri.EscapeDataString(O.TenantId!.Trim())}/oauth2/v2.0/token"), form, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode >= 500)
        {
            throw new HttpRequestException($"Microsoft Entra answered {(int)response.StatusCode}.");
        }

        var token = text.Length > 0 ? JsonNode.Parse(text) : null;
        if (!response.IsSuccessStatusCode || token?["access_token"] is null)
        {
            // "AADSTS65001: The user or administrator has not consented …": the first line is the reason.
            throw new InvalidOperationException(token?["error_description"]?.GetValue<string>()?.Split('\n')[0].Trim() ?? $"Microsoft Entra answered {(int)response.StatusCode}.");
        }

        return token;
    }
}

/// <summary>A personal access token checked with Azure DevOps' <c>connectionData</c> (docs/architect/ado-user-delegation.md §2.1).</summary>
public sealed class AdoPatCheck(HttpClient http, IOptions<AzureDevOpsOptions> options) : IAdoPatCheck
{
    public async Task<DelegatedSignIn> WhoAsync(string pat, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pat);
        try
        {
            return await AdoDelegation.WhoAsync(http, options.Value, PatAuthProvider.Header(pat), pat, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Azure DevOps ({options.Value.Organization}) didn't accept this token. Is it for this organization, unexpired, with Code (Read & write) and Work Items (Read & write)?");
        }
    }
}
