using Agentd.Application.Ports;

namespace Agentd.Infrastructure.AzureDevOps;

/// <summary>
/// git over HTTPS to Azure DevOps with agentd's own sign-in (the Auth setting: the machine's <c>az login</c>, a service
/// principal or a PAT), never a person's delegated token. The header is sent only to Azure DevOps.
/// </summary>
public sealed class AdoGitCredentials(IAdoAuthProvider own) : IGitHttpCredentials
{
    public const string UrlPrefix = "https://dev.azure.com/";

    public async ValueTask<IReadOnlyList<GitHttpHeader>> GetAsync(CancellationToken cancellationToken)
    {
        var auth = await own.GetAsync(false, cancellationToken).ConfigureAwait(false);
        return [new GitHttpHeader(UrlPrefix, $"Authorization: {auth.Scheme} {auth.Parameter}")];
    }
}
