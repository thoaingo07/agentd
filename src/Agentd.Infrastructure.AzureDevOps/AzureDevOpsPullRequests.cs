using System.Globalization;
using System.Text.Json.Nodes;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;
using static Agentd.Infrastructure.AzureDevOps.AdoHttp;

namespace Agentd.Infrastructure.AzureDevOps;

/// <summary>Pull requests on the repository's own organization/project (any registered repo).</summary>
public sealed class AzureDevOpsPullRequests(HttpClient http) : IPullRequestService
{
    private const int MaxDescriptionLength = 4000;

    public async Task<PullRequestRef?> FindOpenAsync(Repository repository, BranchName source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var url = $"{Base(repository)}/pullrequests?searchCriteria.sourceRefName={Esc("refs/heads/" + source.Value)}&searchCriteria.status=active&api-version={ApiVersion}";
        var result = await AdoHttp.GetAsync(http, url, cancellationToken).ConfigureAwait(false);
        var first = result?["value"]?.AsArray().FirstOrDefault();
        return first is null ? null : Ref(repository, first["pullRequestId"]!.GetValue<int>());
    }

    public async Task<PullRequestRef> CreateAsync(Repository repository, BranchName source, string target, string title, string description, WorkItemId workItem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(description);
        var body = new JsonObject
        {
            ["sourceRefName"] = "refs/heads/" + source.Value,
            ["targetRefName"] = "refs/heads/" + target,
            ["title"] = title,
            ["description"] = description.Length > MaxDescriptionLength ? description[..MaxDescriptionLength] : description,
            ["workItemRefs"] = new JsonArray(new JsonObject { ["id"] = workItem.Value.ToString(CultureInfo.InvariantCulture) }),
        };
        var created = await SendAsync(http, HttpMethod.Post, $"{Base(repository)}/pullrequests?api-version={ApiVersion}", body, "application/json", cancellationToken).ConfigureAwait(false)
            ?? throw new AdoException("Creating the pull request returned no body.");
        return Ref(repository, created["pullRequestId"]!.GetValue<int>());
    }

    private static string Base(Repository r) =>
        $"{Esc(r.AzureDevOps.Organization)}/{Esc(r.AzureDevOps.Project)}/_apis/git/repositories/{Esc(r.AzureDevOps.Name)}";

    private PullRequestRef Ref(Repository r, int id) =>
        new(id, new Uri(http.BaseAddress!, $"{Esc(r.AzureDevOps.Organization)}/{Esc(r.AzureDevOps.Project)}/_git/{Esc(r.AzureDevOps.Name)}/pullrequest/{id}"));
}
