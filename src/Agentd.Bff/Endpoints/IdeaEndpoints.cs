using Agentd.Application.Abstractions;
using Agentd.Application.Ideas;
using Agentd.Application.Queries;
using Agentd.Bff.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary>The Ideas page (Phase 2d): brainstormed ideas, read-only. Ideas start and continue in chat (<c>!idea</c>).</summary>
public static class IdeaEndpoints
{
    public static RouteGroupBuilder MapIdeas(this RouteGroupBuilder api)
    {
        api.MapGet("/ideas", async Task<IResult> (int? workItem, [FromServices] IQueryHandler<GetIdeas, IReadOnlyList<IdeaSummary>> handler, CancellationToken ct) =>
            workItem is <= 0
                ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["workItem"] = ["A work item id is positive."] })
                : TypedResults.Ok((await handler.Handle(new GetIdeas(workItem), ct).ConfigureAwait(false)).Select(IdeaSummaryVm.From).ToList()))
            .WithName("GetIdeas").Produces<List<IdeaSummaryVm>>().ProducesValidationProblem();

        api.MapGet("/ideas/{id:long}", async Task<IResult> (long id, [FromServices] IQueryHandler<GetIdea, IdeaDetail?> handler, CancellationToken ct) =>
            await handler.Handle(new GetIdea(id), ct).ConfigureAwait(false) is { } idea
                ? TypedResults.Ok(IdeaDetailVm.From(idea))
                : TypedResults.Problem($"There is no idea {id}.", statusCode: StatusCodes.Status404NotFound, title: "Not found"))
            .WithName("GetIdea").Produces<IdeaDetailVm>().ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }
}
