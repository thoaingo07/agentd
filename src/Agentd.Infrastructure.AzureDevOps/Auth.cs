using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.AzureDevOps;

/// <summary>Produces the Authorization header for Azure DevOps.</summary>
public interface IAdoAuthProvider
{
    ValueTask<AuthenticationHeaderValue> GetAsync(bool forceRefresh, CancellationToken cancellationToken);
}

/// <summary>
/// A Microsoft Entra bearer token, cached until 5 minutes before expiry: the machine's <c>az login</c> by default, or
/// any <see cref="TokenCredential"/> (a service principal, <see cref="ForServicePrincipal"/>).
/// </summary>
public sealed class AzCliAuthProvider(TokenCredential credential) : IAdoAuthProvider, IDisposable
{
    /// <summary>The Azure DevOps resource (application) id.</summary>
    public const string Scope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private AccessToken? _token;

    public AzCliAuthProvider()
        : this(new AzureCliCredential())
    {
    }

    /// <summary>A service principal's client-secret sign-in; missing settings fail on first use with what to set.</summary>
    public static IAdoAuthProvider ForServicePrincipal(string? tenantId, string? clientId, string? clientSecret) =>
        string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)
            ? new MissingAuthProvider("Azure DevOps auth is 'ServicePrincipal' but Agentd:AzureDevOps:TenantId, ClientId or the secret ClientSecret isn't set.")
            : new AzCliAuthProvider(new ClientSecretCredential(tenantId.Trim(), clientId.Trim(), clientSecret.Trim()));

    public async ValueTask<AuthenticationHeaderValue> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (forceRefresh || _token is not { } t || t.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                _token = await credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken).ConfigureAwait(false);
            }

            return new AuthenticationHeaderValue("Bearer", _token.Value.Token);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}

/// <summary>
/// agentd's own auth, except inside an <see cref="Application.AzureDevOps.AdoActor"/> scope: then the person's delegated
/// token (docs/architect/ado-user-delegation.md). A person's call never falls back to agentd once it's under way.
/// </summary>
public sealed class ActingAsAuthProvider(IAdoAuthProvider own, Application.AzureDevOps.AdoActor actor, Application.AzureDevOps.AdoUserTokens tokens) : IAdoAuthProvider
{
    public async ValueTask<AuthenticationHeaderValue> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (actor.Current is not { } person)
        {
            return await own.GetAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        }

        return await tokens.GetAccessTokenAsync(person, forceRefresh, cancellationToken).ConfigureAwait(false) is { } token
            ? new AuthenticationHeaderValue("Bearer", token)
            : throw new AdoException("The person's Azure DevOps sign-in stopped working during this action; they need to reconnect (Settings → Your Azure DevOps).", 401);
    }
}

/// <summary>Auth that isn't configured: every request fails with <paramref name="message"/>.</summary>
internal sealed class MissingAuthProvider(string message) : IAdoAuthProvider
{
    public ValueTask<AuthenticationHeaderValue> GetAsync(bool forceRefresh, CancellationToken cancellationToken) => throw new AdoException(message);
}

/// <summary>Basic auth with a personal access token (empty user name).</summary>
public sealed class PatAuthProvider(IOptions<AzureDevOpsOptions> options) : IAdoAuthProvider
{
    public ValueTask<AuthenticationHeaderValue> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var pat = options.Value.Pat;
        if (string.IsNullOrWhiteSpace(pat))
        {
            throw new AdoException("Azure DevOps auth is 'Pat' but no PAT is configured (Agentd:AzureDevOps:Pat).");
        }

        return ValueTask.FromResult(new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat))));
    }
}

/// <summary>
/// Adds the Authorization header plus two Azure DevOps headers, and on 401 refreshes once and retries
/// (az CLI token rotation):
/// <list type="bullet">
/// <item><c>X-TFS-FedAuthRedirect: Suppress</c>: a rejected credential gets a 401 with a reason instead of a 302 to the sign-in page.</item>
/// <item><c>X-VSS-ForceMsaPassThrough: true</c>: lets a Microsoft-account (MSA) identity that signed in to Entra as a guest
/// (<c>live.com#…</c>) use its MSA identity in MSA-backed organizations. The Azure DevOps SDKs send the same header.</item>
/// </list>
/// </summary>
public sealed class AdoAuthHandler(IAdoAuthProvider auth) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AddAdoHeaders(request);
        request.Headers.Authorization = await auth.GetAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        using var retry = await CloneAsync(request).ConfigureAwait(false);
        AddAdoHeaders(retry);
        retry.Headers.Authorization = await auth.GetAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private static void AddAdoHeaders(HttpRequestMessage request)
    {
        // Set, not append: a value repeated by another handler ("true, true") is not recognized.
        request.Headers.Remove("X-TFS-FedAuthRedirect");
        request.Headers.Remove("X-VSS-ForceMsaPassThrough");
        request.Headers.TryAddWithoutValidation("X-TFS-FedAuthRedirect", "Suppress");
        request.Headers.TryAddWithoutValidation("X-VSS-ForceMsaPassThrough", "true");
        if (!request.Headers.Accept.Any(a => a.MediaType == "application/json"))
        {
            request.Headers.Accept.ParseAdd("application/json");
        }
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(body);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
