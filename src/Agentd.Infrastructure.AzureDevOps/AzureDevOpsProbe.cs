using Agentd.Application.Jobs;
using Agentd.Application.Setup;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.AzureDevOps;

/// <summary>
/// The setup step's Test: a one-off client for settings that may not be saved yet, running the daemon's own WIQL
/// query. Never follows redirects (a redirect means the credential was rejected).
/// </summary>
/// <param name="primary">The transport (tests); default: sockets without redirects.</param>
/// <param name="azCli">The <c>az login</c> credential (tests); default: the machine's az CLI.</param>
/// <param name="principal">Signs a service principal in (tests); default: Entra with its client secret.</param>
public sealed class AzureDevOpsProbe(Func<HttpMessageHandler>? primary = null, IAdoAuthProvider? azCli = null, Func<ServicePrincipal, IAdoAuthProvider>? principal = null) : IAzureDevOpsProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<StepCheck> TestAsync(AzureDevOpsConnection connection, JobOptions jobs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(jobs);
        var options = new AzureDevOpsOptions
        {
            Organization = connection.Organization,
            Project = connection.Project,
            Auth = connection.UsePat ? AzureDevOpsAuth.Pat : connection.Principal is null ? AzureDevOpsAuth.AzCli : AzureDevOpsAuth.ServicePrincipal,
            Pat = connection.Pat,
        };
        var auth = connection switch
        {
            { UsePat: true } => new PatAuthProvider(Options.Create(options)),
            { Principal: { } sp } => principal?.Invoke(sp) ?? AzCliAuthProvider.ForServicePrincipal(sp.TenantId, sp.ClientId, sp.ClientSecret),
            _ => azCli ?? new AzCliAuthProvider(),
        };
        try
        {
            using var handler = new AdoAuthHandler(auth) { InnerHandler = primary?.Invoke() ?? new SocketsHttpHandler { AllowAutoRedirect = false } };
            using var http = new HttpClient(handler) { BaseAddress = options.BaseUrl, Timeout = Timeout };
            var waiting = await new AzureDevOpsWorkItemSource(http, Options.Create(options))
                .QueryTaggedAsync(jobs.Tag, jobs.ClaimTag, [.. jobs.States], cancellationToken).ConfigureAwait(false);
            return new StepCheck(true, $"Signed in to {connection.Organization}/{connection.Project}. {waiting.Count} work item(s) tagged '{jobs.Tag}' are waiting.");
        }
        catch (Exception ex) when (ex is AdoException or HttpRequestException or TaskCanceledException or Azure.Identity.CredentialUnavailableException or Azure.Identity.AuthenticationFailedException
                                   && !cancellationToken.IsCancellationRequested)
        {
            return new StepCheck(false, ex is TaskCanceledException ? "Azure DevOps didn't answer in time." : ex.Message, connection switch
            {
                { UsePat: true } => "check the organization and project names, and that the token has Work Items (read & write), Code (read & write) and Build (read)",
                { Principal: not null } => "check the tenant, client id and secret (not the secret's id), that the organization is connected to this Entra tenant, " +
                    "and that the app is added to the organization (Users) with a Basic license and project access",
                _ => "run `az login` on the server as the user agentd runs as, and check the organization and project names",
            });
        }
        finally
        {
            (auth as IDisposable)?.Dispose();
        }
    }
}
