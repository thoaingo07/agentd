using System.Globalization;
using System.Text.Json.Nodes;
using Agentd.Application.Ports;
using Microsoft.Extensions.Options;
using static Agentd.Infrastructure.AzureDevOps.AdoHttp;

namespace Agentd.Infrastructure.AzureDevOps;

/// <summary>Work item and pull request searches in the configured project (REST 7.1), for <c>!chat</c>.</summary>
public sealed class AzureDevOpsSearch(HttpClient http, IOptions<AzureDevOpsOptions> options) : IAzureDevOpsSearch
{
    public const int MaxTop = 50;

    private string Org => Esc(options.Value.Organization);

    private string Project => Esc(options.Value.Project);

    public async Task<IReadOnlyList<WorkItemHit>> SearchWorkItemsAsync(WorkItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var top = Math.Clamp(query.Top, 1, MaxTop);
        var wiql = new JsonObject { ["query"] = Wiql(query) };
        var result = await SendAsync(http, HttpMethod.Post, $"{Org}/{Project}/_apis/wit/wiql?$top={top}&api-version={ApiVersion}", wiql, "application/json", cancellationToken).ConfigureAwait(false);
        var ids = result?["workItems"]?.AsArray().Select(w => w!["id"]!.GetValue<int>()).Take(top).ToList() ?? [];
        if (ids.Count == 0)
        {
            return [];
        }

        var batch = new JsonObject
        {
            ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()),
            ["fields"] = new JsonArray("System.Id", "System.WorkItemType", "System.Title", "System.State", "System.AssignedTo", "System.Tags", "System.ChangedDate"),
        };
        var items = await SendAsync(http, HttpMethod.Post, $"{Org}/_apis/wit/workitemsbatch?api-version={ApiVersion}", batch, "application/json", cancellationToken).ConfigureAwait(false);
        var byId = (items?["value"]?.AsArray() ?? []).Select(w => w!).ToDictionary(w => w["id"]!.GetValue<int>());
        return [.. ids.Where(byId.ContainsKey).Select(id =>
        {
            var f = byId[id]["fields"]!;
            return new WorkItemHit(id, Text(f["System.WorkItemType"]) ?? "", Text(f["System.Title"]) ?? "", Text(f["System.State"]) ?? "",
                f["System.AssignedTo"]?["displayName"]?.GetValue<string>(),
                (Text(f["System.Tags"]) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                DateTimeOffset.TryParse(Text(f["System.ChangedDate"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null);
        })];
    }

    public async Task<IReadOnlyList<PullRequestHit>> ListPullRequestsAsync(string? repository, string status, int top, CancellationToken cancellationToken)
    {
        var state = status?.ToLowerInvariant() is "completed" or "abandoned" or "all" ? status.ToLowerInvariant() : "active";
        var scope = string.IsNullOrWhiteSpace(repository) ? $"{Org}/{Project}/_apis/git" : $"{Org}/{Project}/_apis/git/repositories/{Esc(repository.Trim())}";
        var result = await AdoHttp.GetAsync(http, $"{scope}/pullrequests?searchCriteria.status={state}&$top={Math.Clamp(top, 1, MaxTop)}&api-version={ApiVersion}", cancellationToken).ConfigureAwait(false);
        return [.. (result?["value"]?.AsArray() ?? []).Select(p => p!).Select(p => new PullRequestHit(
            p["pullRequestId"]!.GetValue<int>(), p["repository"]?["name"]?.GetValue<string>() ?? "", Text(p["title"]) ?? "",
            p["createdBy"]?["displayName"]?.GetValue<string>() ?? "", Text(p["status"]) ?? "",
            Branch(Text(p["sourceRefName"])), Branch(Text(p["targetRefName"])),
            DateTimeOffset.TryParse(Text(p["creationDate"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : default))];
    }

    /// <summary>The WIQL for a search. Values are quoted with '' escaping; the macros @project and @CurrentIteration are the only raw parts.</summary>
    internal static string Wiql(WorkItemQuery q)
    {
        static string Quote(string value) => "'" + value.Trim().Replace("'", "''", StringComparison.Ordinal) + "'";
        var where = new List<string> { "[System.TeamProject] = @project" };
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            where.Add($"[System.Title] CONTAINS {Quote(q.Text)}");
        }

        if (!string.IsNullOrWhiteSpace(q.Type))
        {
            where.Add($"[System.WorkItemType] = {Quote(q.Type)}");
        }

        if (!string.IsNullOrWhiteSpace(q.State))
        {
            where.Add($"[System.State] = {Quote(q.State)}");
        }

        if (!string.IsNullOrWhiteSpace(q.AssignedTo))
        {
            where.Add($"[System.AssignedTo] CONTAINS {Quote(q.AssignedTo)}");
        }

        if (!string.IsNullOrWhiteSpace(q.Tag))
        {
            where.Add($"[System.Tags] CONTAINS {Quote(q.Tag)}");
        }

        if (!string.IsNullOrWhiteSpace(q.AreaPath))
        {
            where.Add($"[System.AreaPath] UNDER {Quote(q.AreaPath)}");
        }

        if (!string.IsNullOrWhiteSpace(q.Iteration))
        {
            where.Add(string.Equals(q.Iteration.Trim(), "@CurrentIteration", StringComparison.OrdinalIgnoreCase)
                ? "[System.IterationPath] = @CurrentIteration"
                : $"[System.IterationPath] UNDER {Quote(q.Iteration)}");
        }

        return $"SELECT [System.Id] FROM WorkItems WHERE {string.Join(" AND ", where)} ORDER BY [System.ChangedDate] DESC";
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Branch(string? reference) => reference?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? reference["refs/heads/".Length..] : reference ?? "";
}
