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
public sealed class AzureDevOpsProbe(Func<HttpMessageHandler>? primary = null, IAdoAuthProvider? azCli = null) : IAzureDevOpsProbe
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
            Auth = connection.UsePat ? AzureDevOpsAuth.Pat : AzureDevOpsAuth.AzCli,
            Pat = connection.Pat,
        };
        var auth = connection.UsePat ? new PatAuthProvider(Options.Create(options)) : azCli ?? new AzCliAuthProvider();
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
            return new StepCheck(false, ex is TaskCanceledException ? "Azure DevOps didn't answer in time." : ex.Message, connection.UsePat
                ? "check the organization and project names, and that the token has Work Items (read & write), Code (read & write) and Build (read)"
                : "run `az login` on the server as the user agentd runs as, and check the organization and project names");
        }
        finally
        {
            (auth as IDisposable)?.Dispose();
        }
    }
}
