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

/// <summary>Bearer token from the machine's <c>az login</c>, cached until 5 minutes before expiry.</summary>
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

/// <summary>Adds the Authorization header; on 401 refreshes once and retries (az CLI token rotation).</summary>
public sealed class AdoAuthHandler(IAdoAuthProvider auth) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = await auth.GetAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        using var retry = await CloneAsync(request).ConfigureAwait(false);
        retry.Headers.Authorization = await auth.GetAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
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
