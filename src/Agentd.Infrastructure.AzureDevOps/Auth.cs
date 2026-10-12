using System.Collections.Concurrent;
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

        return await tokens.GetCredentialAsync(person, forceRefresh, cancellationToken).ConfigureAwait(false) switch
        {
            { IsPat: true, Token: var pat } => PatAuthProvider.Header(pat),
            { Token: var token } => new AuthenticationHeaderValue("Bearer", token),
            null => throw new AdoException("The person's Azure DevOps sign-in stopped working during this action; they need to reconnect (Settings → Your Azure DevOps).", 401),
        };
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

        return ValueTask.FromResult(Header(pat));
    }

    /// <summary>A PAT as Basic auth with an empty user name.</summary>
    internal static AuthenticationHeaderValue Header(string pat) => new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));
}

/// <summary>
/// Adds the Authorization header plus two Azure DevOps headers, and on 401 refreshes once and retries
/// (az CLI token rotation):
/// <list type="bullet">
/// <item><c>X-TFS-FedAuthRedirect: Suppress</c>: a rejected credential gets a 401 with a reason instead of a 302 to the sign-in page.</item>
/// <item><c>X-VSS-ForceMsaPassThrough: true</c>: lets a Microsoft-account (MSA) identity that signed in to Entra as a guest
/// (<c>live.com#…</c>) use its MSA identity in MSA-backed organizations. The Azure DevOps SDKs send the same header.
/// An organization connected to Entra ID refuses it (TF400813), so when the refreshed retry is still 401 the request is
/// tried once more with the header flipped, and what worked is remembered per organization.</item>
/// </list>
/// </summary>
public sealed class AdoAuthHandler(IAdoAuthProvider auth) : DelegatingHandler
{
    /// <summary>Whether each organization takes <c>X-VSS-ForceMsaPassThrough</c>: learned from responses, shared by every handler.</summary>
    private static readonly ConcurrentDictionary<string, bool> s_msaPassThrough = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var organization = Organization(request.RequestUri);
        var msa = s_msaPassThrough.GetValueOrDefault(organization, true);
        AddAdoHeaders(request, msa);
        request.Headers.Authorization = await auth.GetAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        using var retry = await CloneAsync(request).ConfigureAwait(false);
        AddAdoHeaders(retry, msa);
        retry.Headers.Authorization = await auth.GetAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        response = await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        using var flipped = await CloneAsync(request).ConfigureAwait(false);
        AddAdoHeaders(flipped, !msa);
        flipped.Headers.Authorization = retry.Headers.Authorization;
        response = await base.SendAsync(flipped, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            s_msaPassThrough[organization] = !msa;
        }

        return response;
    }

    /// <summary>The organization a request goes to: <c>[vssps.]dev.azure.com/{org}</c>, or the host (<c>{org}.visualstudio.com</c>).</summary>
    internal static string Organization(Uri? uri) =>
        uri is null ? string.Empty
        : uri.Host.EndsWith("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? uri.Host + "/" + uri.Segments.ElementAtOrDefault(1)?.Trim('/')
        : uri.Host;

    private static void AddAdoHeaders(HttpRequestMessage request, bool msaPassThrough)
    {
        // Set, not append: a value repeated by another handler ("true, true") is not recognized.
        request.Headers.Remove("X-TFS-FedAuthRedirect");
        request.Headers.Remove("X-VSS-ForceMsaPassThrough");
        request.Headers.TryAddWithoutValidation("X-TFS-FedAuthRedirect", "Suppress");
        if (msaPassThrough)
        {
            request.Headers.TryAddWithoutValidation("X-VSS-ForceMsaPassThrough", "true");
        }

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
