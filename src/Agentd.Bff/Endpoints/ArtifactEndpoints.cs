using System.Text.RegularExpressions;
using Agentd.Application.Artifacts;
using Agentd.Bff.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary>
/// Artifacts (docs/architect/artifacts.md): <c>GET /api/artifacts</c> lists them, and each page is served at
/// <c>/artifacts/&lt;name&gt;.html</c> to anyone signed in to the Web UI, in a CSP sandbox (<see cref="SecurityHeaders.ArtifactPolicy"/>).
/// </summary>
public static partial class ArtifactEndpoints
{
    public const int ListLimit = 200;

    public static RouteGroupBuilder MapArtifacts(this RouteGroupBuilder api)
    {
        api.MapGet("/artifacts", async ([FromServices] IArtifactStore store, CancellationToken ct) =>
            TypedResults.Ok((await store.ListAsync(ListLimit, ct).ConfigureAwait(false))
                .Select(a => new ArtifactVm(a.Name, a.Title, $"{SecurityHeaders.ArtifactsPath}/{a.Name}.html", a.Source, a.SourceId, a.Author, a.CreatedAt, a.UpdatedAt)).ToList()))
            .WithName("ListArtifacts");
        return api;
    }

    public static IEndpointRouteBuilder MapArtifactPages(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(SecurityHeaders.ArtifactsPath + "/{file}", async Task<IResult> (string file, [FromServices] IArtifactStore store, CancellationToken ct) =>
            File().Match(file) is { Success: true } m && await store.GetAsync(m.Groups["name"].Value, ct).ConfigureAwait(false) is { } artifact
                ? artifact.Html is { } html
                    ? TypedResults.Content(html, "text/html; charset=utf-8")
                    : TypedResults.Content(Expired(artifact), "text/html; charset=utf-8", statusCode: StatusCodes.Status410Gone)
                : TypedResults.NotFound())
            .RequireAuthorization().ExcludeFromDescription();
        return endpoints;
    }

    /// <summary>What an expired page's link shows (a link in a PR or chat outlives the page).</summary>
    private static string Expired(Artifact a) =>
        "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>Expired: " + System.Net.WebUtility.HtmlEncode(a.Title) + "</title>\n</head>\n<body>\n" +
        "<h1>" + System.Net.WebUtility.HtmlEncode(a.Title) + "</h1>\n<p>This artifact expired" +
        (a.ExpiredAt is { } at ? " on " + at.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : string.Empty) +
        ". Ask the agent to publish it again.</p>\n</body>\n</html>\n";

    [GeneratedRegex("^(?<name>[a-z0-9][a-z0-9-]{0,63})\\.html$")]
    private static partial Regex File();
}

/// <summary>An artifact: open <c>url</c> (a page of its own); <c>source</c> (job, chat, idea, review, review-session, watch, web) and <c>sourceId</c> say which agent published it.</summary>
public sealed record ArtifactVm(string Name, string Title, string Url, string Source, long SourceId, string Author, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
