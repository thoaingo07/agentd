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

    public async Task<DelegatedSignIn> RedeemAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = O.ClientId!.Trim(),
            ["client_secret"] = O.ClientSecret!.Trim(),
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri.ToString(),
            ["code_verifier"] = codeVerifier,
            ["scope"] = Scopes,
        });
        using var tokenResponse = await http.PostAsync(new Uri(Authority, $"{Uri.EscapeDataString(O.TenantId!.Trim())}/oauth2/v2.0/token"), form, cancellationToken).ConfigureAwait(false);
        var token = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!tokenResponse.IsSuccessStatusCode || token?["access_token"]?.GetValue<string>() is not { } access)
        {
            // "AADSTS65001: The user or administrator has not consented …": the first line is the reason; never the request.
            throw new InvalidOperationException(token?["error_description"]?.GetValue<string>()?.Split('\n')[0].Trim() ?? $"Microsoft Entra answered {(int)tokenResponse.StatusCode}.");
        }

        var refresh = token["refresh_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Microsoft Entra didn't return a refresh token (the app needs offline_access).");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(O.BaseUrl, $"{Uri.EscapeDataString(O.Organization)}/_apis/connectionData"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var me = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var user = me.IsSuccessStatusCode && me.Content.Headers.ContentType?.MediaType == "application/json"
            ? JsonNode.Parse(await me.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))?["authenticatedUser"]
            : null;
        if (user?["id"]?.GetValue<string>() is not { } id || !Guid.TryParse(id, out var identity) || identity == Guid.Empty)
        {
            throw new InvalidOperationException($"Azure DevOps ({O.Organization}) didn't accept this account. Is it a member of the organization?");
        }

        var name = user["providerDisplayName"]?.GetValue<string>() ?? user["customDisplayName"]?.GetValue<string>() ?? "";
        var unique = user["properties"]?["Account"]?["$value"]?.GetValue<string>() ?? name;
        return new DelegatedSignIn(identity, unique, name.Length > 0 ? name : unique, refresh);
    }
}
