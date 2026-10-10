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

    public async Task<PullRequestStatus> GetStatusAsync(Repository repository, int pullRequestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var pr = await AdoHttp.GetAsync(http, $"{Base(repository)}/pullrequests/{pullRequestId}?api-version={ApiVersion}", cancellationToken).ConfigureAwait(false)
            ?? throw new AdoException($"Pull request {pullRequestId} was not found.", 404);
        return pr["status"]?.GetValue<string>() switch
        {
            "completed" => PullRequestStatus.Completed,
            "abandoned" => PullRequestStatus.Abandoned,
            _ => PullRequestStatus.Active,
        };
    }

    public async Task<IReadOnlyList<PullRequestComment>> ListCommentsAsync(Repository repository, int pullRequestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var result = await AdoHttp.GetAsync(http, $"{Base(repository)}/pullrequests/{pullRequestId}/threads?api-version={ApiVersion}", cancellationToken).ConfigureAwait(false);
        var comments = new List<PullRequestComment>();
        foreach (var thread in result?["value"]?.AsArray().OfType<JsonNode>() ?? [])
        {
            if (thread["isDeleted"]?.GetValue<bool>() == true)
            {
                continue;
            }

            var context = thread["threadContext"];
            var line = context?["rightFileStart"]?["line"]?.GetValue<int>() ?? context?["leftFileStart"]?["line"]?.GetValue<int>();
            foreach (var comment in thread["comments"]?.AsArray().OfType<JsonNode>() ?? [])
            {
                var content = comment["content"]?.GetValue<string>();
                if (comment["commentType"]?.GetValue<string>() != "text" || comment["isDeleted"]?.GetValue<bool>() == true
                    || string.IsNullOrWhiteSpace(content) || content.StartsWith(PullRequestComment.AgentdMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                comments.Add(new PullRequestComment(
                    thread["id"]!.GetValue<int>(),
                    comment["id"]!.GetValue<int>(),
                    comment["author"]?["displayName"]?.GetValue<string>() ?? "someone",
                    content,
                    context?["filePath"]?.GetValue<string>(),
                    line,
                    thread["status"]?.GetValue<string>() ?? "active",
                    comment["publishedDate"] is { } at ? DateTimeOffset.Parse(at.GetValue<string>(), CultureInfo.InvariantCulture) : DateTimeOffset.MinValue,
                    comment["author"]?["uniqueName"]?.GetValue<string>()));
            }
        }

        return comments.OrderBy(c => c.PublishedAt).ThenBy(c => c.CommentId).ToList();
    }

    public async Task ReplyAsync(Repository repository, int pullRequestId, int threadId, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var body = new JsonObject { ["content"] = $"{PullRequestComment.AgentdMarker} {text}", ["parentCommentId"] = 1, ["commentType"] = 1 };
        await SendAsync(http, HttpMethod.Post, $"{Base(repository)}/pullrequests/{pullRequestId}/threads/{threadId}/comments?api-version={ApiVersion}", body, "application/json", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PullRequestDetails?> GetAsync(Repository repository, int pullRequestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        JsonNode? pr;
        try
        {
            pr = await AdoHttp.GetAsync(http, $"{Base(repository)}/pullrequests/{pullRequestId}?api-version={ApiVersion}", cancellationToken).ConfigureAwait(false);
        }
        catch (AdoException ex) when (ex.StatusCode == 404)
        {
            return null;
        }

        if (pr is null)
        {
            return null;
        }

        static string Branch(JsonNode? name) => (name?.GetValue<string>() ?? string.Empty).Replace("refs/heads/", string.Empty, StringComparison.Ordinal);
        return new PullRequestDetails(
            pullRequestId,
            pr["title"]?.GetValue<string>() ?? $"PR {pullRequestId}",
            pr["description"]?.GetValue<string>(),
            pr["createdBy"]?["displayName"]?.GetValue<string>() ?? "someone",
            Branch(pr["sourceRefName"]),
            Branch(pr["targetRefName"]),
            pr["lastMergeSourceCommit"]?["commitId"]?.GetValue<string>() ?? throw new AdoException($"Pull request {pullRequestId} has no source commit."),
            pr["status"]?.GetValue<string>() switch { "completed" => PullRequestStatus.Completed, "abandoned" => PullRequestStatus.Abandoned, _ => PullRequestStatus.Active },
            pr["isDraft"]?.GetValue<bool>() ?? false,
            Ref(repository, pullRequestId).Url,
            await LinkedWorkItemsAsync(repository, pullRequestId, cancellationToken).ConfigureAwait(false),
            pr["mergeStatus"]?.GetValue<string>());
    }

    /// <summary>The PR's linked work items; best effort (a review works without them).</summary>
    private async Task<IReadOnlyList<int>> LinkedWorkItemsAsync(Repository repository, int pullRequestId, CancellationToken ct)
    {
        try
        {
            var linked = await AdoHttp.GetAsync(http, $"{Base(repository)}/pullrequests/{pullRequestId}/workitems?api-version={ApiVersion}", ct).ConfigureAwait(false);
            return [.. linked?["value"]?.AsArray().OfType<JsonNode>()
                .Select(w => int.TryParse(w["id"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(id => id > 0) ?? []];
        }
        catch (AdoException)
        {
            return [];
        }
    }

    public async Task<int> CreateThreadAsync(Repository repository, int pullRequestId, string text, string? filePath, int? line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(text);
        var body = new JsonObject
        {
            ["comments"] = new JsonArray(new JsonObject { ["parentCommentId"] = 0, ["content"] = $"{PullRequestComment.AgentdMarker} {text}", ["commentType"] = 1 }),
            ["status"] = 1,   // active
        };
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            // ADO paths start with '/'; the line is on the PR's new (right) side.
            var context = new JsonObject { ["filePath"] = "/" + filePath.TrimStart('/') };
            if (line is > 0 and var at)
            {
                context["rightFileStart"] = new JsonObject { ["line"] = at, ["offset"] = 1 };
                context["rightFileEnd"] = new JsonObject { ["line"] = at, ["offset"] = 1 };
            }

            body["threadContext"] = context;
        }

        var created = await SendAsync(http, HttpMethod.Post, $"{Base(repository)}/pullrequests/{pullRequestId}/threads?api-version={ApiVersion}", body, "application/json", cancellationToken).ConfigureAwait(false)
            ?? throw new AdoException("Creating the thread returned no body.");
        return created["id"]!.GetValue<int>();
    }

    public async Task UpdateThreadTextAsync(Repository repository, int pullRequestId, int threadId, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(text);
        // The thread's first comment (id 1) is the one agentd opened it with.
        var body = new JsonObject { ["content"] = $"{PullRequestComment.AgentdMarker} {text}" };
        await SendAsync(http, HttpMethod.Patch, $"{Base(repository)}/pullrequests/{pullRequestId}/threads/{threadId}/comments/1?api-version={ApiVersion}", body, "application/json", cancellationToken).ConfigureAwait(false);
    }

    public async Task SetThreadStatusAsync(Repository repository, int pullRequestId, int threadId, PullRequestThreadStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var body = new JsonObject { ["status"] = (int)status };
        await SendAsync(http, HttpMethod.Patch, $"{Base(repository)}/pullrequests/{pullRequestId}/threads/{threadId}?api-version={ApiVersion}", body, "application/json", cancellationToken).ConfigureAwait(false);
    }

    private static string Base(Repository r) =>
        $"{Esc(r.AzureDevOps.Organization)}/{Esc(r.AzureDevOps.Project)}/_apis/git/repositories/{Esc(r.AzureDevOps.Name)}";

    private PullRequestRef Ref(Repository r, int id) =>
        new(id, new Uri(http.BaseAddress!, $"{Esc(r.AzureDevOps.Organization)}/{Esc(r.AzureDevOps.Project)}/_git/{Esc(r.AzureDevOps.Name)}/pullrequest/{id}"));
}
