using System.Globalization;
using System.Text.Json.Nodes;
using Agentd.Application.Ports;
using Microsoft.Extensions.Options;
using static Agentd.Infrastructure.AzureDevOps.AdoHttp;

namespace Agentd.Infrastructure.AzureDevOps;

/// <summary>Work item and pull request searches in the configured project (REST 7.1), for <c>!chat</c>.</summary>
public sealed partial class AzureDevOpsSearch(HttpClient http, IOptions<AzureDevOpsOptions> options) : IAzureDevOpsSearch
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

    public async Task<IReadOnlyList<PipelineHit>> ListPipelinesAsync(string? name, CancellationToken cancellationToken)
    {
        var filter = string.IsNullOrWhiteSpace(name) ? string.Empty : $"&name=*{Esc(name.Trim())}*";
        var result = await AdoHttp.GetAsync(http, $"{Org}/{Project}/_apis/build/definitions?queryOrder=definitionNameAscending&$top=200{filter}&api-version={ApiVersion}", cancellationToken).ConfigureAwait(false);
        return [.. (result?["value"]?.AsArray() ?? []).Select(d => d!).Select(d => new PipelineHit(d["id"]!.GetValue<int>(), Text(d["name"]) ?? "", Text(d["path"]) ?? "\\"))];
    }

    public async Task<IReadOnlyList<BuildHit>> ListBuildsAsync(string? pipeline, string? branch, string? result, int top, CancellationToken cancellationToken)
    {
        var query = new List<string> { "queryOrder=queueTimeDescending", $"$top={Math.Clamp(top, 1, MaxTop)}" };
        if (!string.IsNullOrWhiteSpace(pipeline))
        {
            var ids = int.TryParse(pipeline.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? [id]
                : (await ListPipelinesAsync(pipeline, cancellationToken).ConfigureAwait(false)).Select(p => p.Id).ToList();
            if (ids.Count == 0)
            {
                return [];
            }

            query.Add($"definitions={string.Join(',', ids.Take(20))}");
        }

        if (!string.IsNullOrWhiteSpace(branch))
        {
            var b = branch.Trim();
            query.Add($"branchName={Esc(b.StartsWith("refs/", StringComparison.Ordinal) ? b : "refs/heads/" + b)}");
        }

        var wanted = result?.Trim().ToLowerInvariant();
        if (wanted is "succeeded" or "failed" or "canceled" or "partiallysucceeded")
        {
            query.Add($"resultFilter={(wanted == "partiallysucceeded" ? "partiallySucceeded" : wanted)}");
        }

        var builds = await AdoHttp.GetAsync(http, $"{Org}/{Project}/_apis/build/builds?{string.Join('&', query)}&api-version={ApiVersion}", cancellationToken).ConfigureAwait(false);
        return [.. (builds?["value"]?.AsArray() ?? []).Select(b => Build(b!))];
    }

    public async Task<BuildDetail?> GetBuildAsync(int id, CancellationToken cancellationToken)
    {
        var build = await AdoHttp.GetAsync(http, $"{Org}/{Project}/_apis/build/builds/{id}?api-version={ApiVersion}", cancellationToken).ConfigureAwait(false);
        if (build is null)
        {
            return null;
        }

        var timeline = await AdoHttp.GetAsync(http, $"{Org}/{Project}/_apis/build/builds/{id}/timeline?api-version={ApiVersion}", cancellationToken).ConfigureAwait(false);
        var records = (timeline?["records"]?.AsArray() ?? []).Select(r => r!)
            .Where(r => Text(r["result"]) is "failed" or "canceled" || (r["issues"]?.AsArray().Any(i => Text(i?["type"]) == "error") ?? false))
            .OrderBy(r => Text(r["type"]) == "Task" ? 0 : 1)   // the steps first, then the jobs and stages they failed
            .Take(MaxFailures)
            .ToList();
        var failures = new List<BuildFailure>();
        foreach (var r in records)
        {
            var issues = (r["issues"]?.AsArray() ?? []).Select(i => i!).Where(i => Text(i["type"]) == "error")
                .Select(i => (Text(i["message"]) ?? "").Trim()).Where(m => m.Length > 0).Take(10).ToList();
            string? tail = null;
            if (Text(r["type"]) == "Task" && r["log"]?["id"]?.GetValue<int>() is { } logId)
            {
                tail = Tail(await AdoHttp.GetTextAsync(http, $"{Org}/{Project}/_apis/build/builds/{id}/logs/{logId}?api-version={ApiVersion}", cancellationToken).ConfigureAwait(false));
            }

            failures.Add(new BuildFailure(Text(r["name"]) ?? "", Text(r["type"]) ?? "", Text(r["result"]) ?? "", issues, tail));
        }

        return new BuildDetail(Build(build), failures);
    }

    public async Task<IReadOnlyList<WikiHit>> SearchWikiAsync(string text, int top, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var body = new JsonObject
        {
            ["searchText"] = text.Trim(),
            ["$top"] = Math.Clamp(top, 1, MaxTop),
            ["$skip"] = 0,
            ["filters"] = new JsonObject { [ProjectFilter] = new JsonArray(options.Value.Project) },
        };
        var result = await SendAsync(http, HttpMethod.Post, $"{SearchHost(options.Value.BaseUrl)}{Org}/{Project}/_apis/search/wikisearchresults?api-version={ApiVersion}",
            body, "application/json", cancellationToken).ConfigureAwait(false);
        return [.. (result?["results"]?.AsArray() ?? []).Select(r => r!).Select(r => new WikiHit(
            Text(r["wiki"]?["name"]) ?? "", Text(r["path"]) ?? Text(r["fileName"]) ?? "",
            [.. (r["hits"]?.AsArray() ?? []).SelectMany(h => h?["highlights"]?.AsArray() ?? []).Select(x => Unmark(Text(x))).Where(x => x.Length > 0).Take(3)]))];
    }

    public async Task<WikiPage?> GetWikiPageAsync(string? wiki, string? path, CancellationToken cancellationToken)
    {
        var wikis = (await AdoHttp.GetAsync(http, $"{Org}/{Project}/_apis/wiki/wikis?api-version={ApiVersion}", cancellationToken).ConfigureAwait(false))?["value"]?.AsArray()
            .Select(w => w!).ToList() ?? [];
        var chosen = string.IsNullOrWhiteSpace(wiki)
            ? wikis.FirstOrDefault(w => Text(w["type"]) == "projectWiki") ?? wikis.FirstOrDefault()
            : wikis.FirstOrDefault(w => string.Equals(Text(w["name"]), wiki.Trim(), StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            return null;
        }

        var name = Text(chosen["name"]) ?? "";
        var page = string.IsNullOrWhiteSpace(path) ? "/" : "/" + path.Trim().TrimStart('/');
        var node = await AdoHttp.GetAsync(http,
            $"{Org}/{Project}/_apis/wiki/wikis/{Esc(Text(chosen["id"]) ?? name)}/pages?path={Esc(page)}&recursionLevel=oneLevel&includeContent=true&api-version={ApiVersion}",
            cancellationToken).ConfigureAwait(false);
        return node is null ? null : new WikiPage(name, Text(node["path"]) ?? page, Text(node["content"]),
            [.. (node["subPages"]?.AsArray() ?? []).Select(p => Text(p?["path"])).OfType<string>()],
            [.. wikis.Select(w => Text(w["name"])).OfType<string>()]);
    }

    /// <summary>The search API's filter name for the project.</summary>
    private const string ProjectFilter = "Project";

    /// <summary>Azure DevOps Services serves search from <c>almsearch.dev.azure.com</c>; a server (or another base) serves it itself.</summary>
    internal static Uri SearchHost(Uri baseUrl) =>
        string.Equals(baseUrl.Host, "dev.azure.com", StringComparison.OrdinalIgnoreCase) ? new Uri("https://almsearch.dev.azure.com/") : baseUrl;

    /// <summary>Search highlights wrap matches in <c>&lt;highlighthit&gt;</c>; the agent gets plain text.</summary>
    private static string Unmark(string? highlight) =>
        (highlight ?? "").Replace("<highlighthit>", "", StringComparison.Ordinal).Replace("</highlighthit>", "", StringComparison.Ordinal).ReplaceLineEndings(" ").Trim();

    /// <summary>At most this many failed records per run, and log lines per failed step.</summary>
    public const int MaxFailures = 5;

    public const int LogTailLines = 40;

    /// <summary>The last lines of a log, without the agent's timestamps (<c>2026-10-08T10:00:00.1234567Z </c>).</summary>
    internal static string? Tail(string? log)
    {
        if (string.IsNullOrWhiteSpace(log))
        {
            return null;
        }

        var lines = log.ReplaceLineEndings("\n").TrimEnd().Split('\n');
        return string.Join('\n', lines.TakeLast(LogTailLines).Select(l => LogTimestamp().Replace(l, string.Empty, 1)));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}-\d\d-\d\dT[\d:.]+Z\s?")]
    private static partial System.Text.RegularExpressions.Regex LogTimestamp();

    private static BuildHit Build(JsonNode b) => new(
        b["id"]!.GetValue<int>(), Text(b["definition"]?["name"]) ?? "", Text(b["buildNumber"]) ?? "", Text(b["status"]) ?? "", Text(b["result"]),
        Branch(Text(b["sourceBranch"])), b["requestedFor"]?["displayName"]?.GetValue<string>(), Text(b["reason"]) ?? "", Text(b["sourceVersion"]),
        Date(b["startTime"]), Date(b["finishTime"]));

    private static DateTimeOffset? Date(JsonNode? node) =>
        DateTimeOffset.TryParse(Text(node), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

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
