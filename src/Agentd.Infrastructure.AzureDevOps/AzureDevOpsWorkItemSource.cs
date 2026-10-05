using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Agentd.Application.Ports;
using Microsoft.Extensions.Options;
using static Agentd.Infrastructure.AzureDevOps.AdoHttp;

namespace Agentd.Infrastructure.AzureDevOps;

/// <summary>Work items in the configured organization/project via the Azure DevOps REST API (7.1).</summary>
public sealed class AzureDevOpsWorkItemSource(HttpClient http, IOptions<AzureDevOpsOptions> options) : IWorkItemSource
{
    private const int BatchSize = 200;
    private const string JsonPatch = "application/json-patch+json";

    private string Org => Esc(options.Value.Organization);

    private string Project => Esc(options.Value.Project);

    public async Task<IReadOnlyList<WorkItemRef>> QueryTaggedAsync(string tag, string excludeTag, IReadOnlyList<string> states, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(states);
        var wiql = new JsonObject { ["query"] = BuildWiql(tag, excludeTag, states) };
        var result = await SendAsync(http, HttpMethod.Post, $"{Org}/{Project}/_apis/wit/wiql?api-version={ApiVersion}", wiql, "application/json", cancellationToken).ConfigureAwait(false);
        var ids = result?["workItems"]?.AsArray().Select(w => w!["id"]!.GetValue<int>()).ToList() ?? [];

        var refs = new List<WorkItemRef>(ids.Count);
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            var batch = new JsonObject
            {
                ["ids"] = new JsonArray(chunk.Select(i => (JsonNode)i).ToArray()),
                ["fields"] = new JsonArray("System.Id", "System.Rev"),
            };
            var items = await SendAsync(http, HttpMethod.Post, $"{Org}/_apis/wit/workitemsbatch?api-version={ApiVersion}", batch, "application/json", cancellationToken).ConfigureAwait(false);
            refs.AddRange(items?["value"]?.AsArray().Select(w => new WorkItemRef(w!["id"]!.GetValue<int>(), w["rev"]!.GetValue<int>())) ?? []);
        }

        return refs;
    }

    public async Task<WorkItemDetails?> GetAsync(int id, CancellationToken cancellationToken)
    {
        var item = await AdoHttp.GetAsync(http, $"{Org}/_apis/wit/workitems/{id}?$expand=all&api-version={ApiVersion}", cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var fields = item["fields"]!;
        var comments = await AdoHttp.GetAsync(http, $"{Org}/{Project}/_apis/wit/workItems/{id}/comments?api-version={CommentsApiVersion}", cancellationToken).ConfigureAwait(false);

        return new WorkItemDetails(
            id,
            item["rev"]!.GetValue<int>(),
            Str(fields, "System.Title") ?? string.Empty,
            Str(fields, "System.State") ?? string.Empty,
            Str(fields, "System.AreaPath") ?? string.Empty,
            SplitTags(Str(fields, "System.Tags")),
            HtmlText.ToText(Str(fields, "System.Description")),
            HtmlText.ToText(Str(fields, "Microsoft.VSTS.Common.AcceptanceCriteria")),
            HtmlText.ToText(Str(fields, "Microsoft.VSTS.TCM.ReproSteps")),
            comments?["comments"]?.AsArray()
                .Select(c => new WorkItemComment(
                    c!["createdBy"]?["displayName"]?.GetValue<string>() ?? "unknown",
                    DateTimeOffset.Parse(c["createdDate"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                    HtmlText.ToText(c["text"]?.GetValue<string>()) ?? string.Empty))
                .ToList() ?? [],
            item["_links"]?["html"]?["href"]?.GetValue<string>() is { } href ? new Uri(href) : null);
    }

    public async Task<CreatedWorkItem> CreateAsync(NewWorkItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var patch = new JsonArray();
        void Field(string name, JsonNode? value)
        {
            if (value is not null)
            {
                patch.Add(new JsonObject { ["op"] = "add", ["path"] = "/fields/" + name, ["value"] = value });
            }
        }

        Field("System.Title", item.Title);
        Field("System.Description", HtmlText.FromText(item.Description));
        Field("Microsoft.VSTS.Common.AcceptanceCriteria", HtmlText.FromText(item.AcceptanceCriteria));
        Field("System.Tags", item.Tags.Count > 0 ? string.Join("; ", item.Tags) : null);
        Field("System.AreaPath", item.AreaPath);
        if (item.Estimate is { } estimate)
        {
            if (item.Type == "Task")
            {
                Field("Microsoft.VSTS.Scheduling.OriginalEstimate", estimate);
                Field("Microsoft.VSTS.Scheduling.RemainingWork", estimate);
            }
            else
            {
                Field("Microsoft.VSTS.Scheduling.StoryPoints", estimate);
            }
        }

        if (item.ParentId is { } parent)
        {
            patch.Add(new JsonObject
            {
                ["op"] = "add",
                ["path"] = "/relations/-",
                ["value"] = new JsonObject { ["rel"] = "System.LinkTypes.Hierarchy-Reverse", ["url"] = new Uri(http.BaseAddress!, $"{Org}/_apis/wit/workItems/{parent}").ToString() },
            });
        }

        var created = await SendAsync(http, HttpMethod.Post, $"{Org}/{Project}/_apis/wit/workitems/${Uri.EscapeDataString(item.Type)}?api-version={ApiVersion}", patch, JsonPatch, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Azure DevOps returned no work item.");
        var html = created["_links"]?["html"]?["href"]?.GetValue<string>();
        return new CreatedWorkItem(created["id"]!.GetValue<int>(), html is null ? null : new Uri(html));
    }

    public async Task<bool> TryClaimAsync(int id, int rev, string claimTag, CancellationToken cancellationToken)
    {
        var current = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Rev != rev)
        {
            return false;
        }

        var tags = current.Tags.Contains(claimTag, StringComparer.OrdinalIgnoreCase) ? current.Tags : [.. current.Tags, claimTag];
        var patch = new JsonArray(
            new JsonObject { ["op"] = "test", ["path"] = "/rev", ["value"] = rev },
            new JsonObject { ["op"] = "add", ["path"] = "/fields/System.Tags", ["value"] = string.Join("; ", tags) });

        var (status, body) = await SendRawAsync(http, HttpMethod.Patch, $"{Org}/_apis/wit/workitems/{id}?api-version={ApiVersion}", patch, JsonPatch, cancellationToken).ConfigureAwait(false);
        if ((int)status is >= 200 and < 300)
        {
            return true;
        }

        // A failed /rev test (someone changed the item meanwhile) is a lost race, not an error.
        if (status is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed
            || (status == HttpStatusCode.BadRequest && body.Contains("test", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        throw new AdoException($"Claiming work item {id} failed with {(int)status}.", (int)status);
    }

    public async Task AddCommentAsync(int id, string text, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["text"] = WebUtility.HtmlEncode(text) };
        await SendAsync(http, HttpMethod.Post, $"{Org}/{Project}/_apis/wit/workItems/{id}/comments?api-version={CommentsApiVersion}", body, "application/json", cancellationToken).ConfigureAwait(false);
    }

    internal static string BuildWiql(string tag, string excludeTag, IReadOnlyList<string> states)
    {
        static string Q(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var stateList = string.Join(", ", states.Select(Q));
        return "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project"
            + $" AND [System.Tags] CONTAINS {Q(tag)}"
            + $" AND NOT [System.Tags] CONTAINS {Q(excludeTag)}"
            + (states.Count > 0 ? $" AND [System.State] IN ({stateList})" : string.Empty)
            + " ORDER BY [System.ChangedDate] ASC";
    }

    private static string? Str(JsonNode fields, string name) => fields[name]?.GetValue<string>();

    private static List<string> SplitTags(string? tags) =>
        tags?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];
}
