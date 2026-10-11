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
                .Select(a => new ArtifactVm(a.Name, a.Title, $"{SecurityHeaders.ArtifactsPath}/{a.Name}.html", a.JobId, a.ChatId, a.Author, a.CreatedAt, a.UpdatedAt)).ToList()))
            .WithName("ListArtifacts");
        return api;
    }

    public static IEndpointRouteBuilder MapArtifactPages(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(SecurityHeaders.ArtifactsPath + "/{file}", async Task<IResult> (string file, [FromServices] IArtifactStore store, CancellationToken ct) =>
            File().Match(file) is { Success: true } m && await store.GetAsync(m.Groups["name"].Value, ct).ConfigureAwait(false) is { } artifact
                ? TypedResults.Content(artifact.Html, "text/html; charset=utf-8")
                : TypedResults.NotFound())
            .RequireAuthorization().ExcludeFromDescription();
        return endpoints;
    }

    [GeneratedRegex("^(?<name>[a-z0-9][a-z0-9-]{0,63})\\.html$")]
    private static partial Regex File();
}

/// <summary>An artifact: open <c>url</c> (a page of its own); <c>jobId</c> / <c>chatId</c> say which agent published it.</summary>
public sealed record ArtifactVm(string Name, string Title, string Url, long? JobId, long? ChatId, string Author, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
