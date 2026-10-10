using System.Security.Claims;
using Agentd.Application.Abstractions;
using Agentd.Application.Ideas;
using Agentd.Application.Queries;
using Agentd.Bff.Http;
using Agentd.Bff.ViewModels;
using Agentd.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary>
/// The Ideas page: brainstormed ideas, started and talked through here or in chat (<c>!idea</c>). A message from the page
/// is handled like one in the idea's thread, so the choices (create, create and start, change, discard) are messages too.
/// </summary>
public static class IdeaEndpoints
{
    public static RouteGroupBuilder MapIdeas(this RouteGroupBuilder api)
    {
        api.MapGet("/ideas", async Task<IResult> (int? workItem, [FromServices] IQueryHandler<GetIdeas, IReadOnlyList<IdeaSummary>> handler, CancellationToken ct) =>
            workItem is <= 0
                ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["workItem"] = ["A work item id is positive."] })
                : TypedResults.Ok((await handler.Handle(new GetIdeas(workItem), ct).ConfigureAwait(false)).Select(IdeaSummaryVm.From).ToList()))
            .WithName("GetIdeas").Produces<List<IdeaSummaryVm>>().ProducesValidationProblem();

        api.MapGet("/ideas/{id:long}", async Task<IResult> (long id, [FromServices] IQueryHandler<GetIdea, IdeaDetail?> handler, [FromServices] IdeaService? service, CancellationToken ct) =>
            await handler.Handle(new GetIdea(id), ct).ConfigureAwait(false) is { } idea
                ? TypedResults.Ok(IdeaDetailVm.From(idea) with { Thinking = service?.IsThinking(id) ?? false })
                : TypedResults.Problem($"There is no idea {id}.", statusCode: StatusCodes.Status404NotFound, title: "Not found"))
            .WithName("GetIdea").Produces<IdeaDetailVm>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/ideas", async (StartIdeaRequest? body, ClaimsPrincipal user, [FromServices] IdeaService service, CancellationToken ct) =>
            (await service.StartOnWebAsync(Author(user), body?.Text ?? string.Empty, Blank(body?.Repo), Blank(body?.Model), Blank(body?.Effort), ct).ConfigureAwait(false))
                .ToHttpResult(id => TypedResults.Created($"/api/ideas/{id}", new IdeaStartedVm(id))))
            .WithName("StartIdea").Produces<IdeaStartedVm>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/ideas/{id:long}/messages", async Task<IResult> (long id, IdeaMessageRequest? body, ClaimsPrincipal user, [FromServices] IdeaService service, [FromServices] IIdeaStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Text) || body.Text.Length > JobActionEndpoints.MaxMessageLength)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["text"] = [$"text must be 1 to {JobActionEndpoints.MaxMessageLength} characters."] });
            }

            if (await store.GetAsync(id, ct).ConfigureAwait(false) is not { } idea)
            {
                return DomainError.NotFound($"Idea {id}").ToProblem();
            }

            return await service.HandleMessageAsync(idea, Author(user), body.Text.Trim(), ct, fromWeb: true).ConfigureAwait(false)
                ? TypedResults.Accepted($"/api/ideas/{id}")
                : DomainError.Conflict($"Idea {id} is finished; start a new one.").ToProblem();
        }).WithName("SendIdeaMessage").Produces(StatusCodes.Status202Accepted).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/ideas/{id:long}/settings", async Task<IResult> (long id, IdeaSettingsRequest? body, [FromServices] IdeaService service, [FromServices] IIdeaStore store, CancellationToken ct) =>
        {
            var (model, effort) = (Blank(body?.Model), Blank(body?.Effort));
            if ((model is not null && !BrainstormSettings.IsModel(model)) || (effort is not null && !BrainstormSettings.IsEffort(effort)))
            {
                return DomainError.Validation($"Use a model name (fable, opus, sonnet or a full name) and an effort level ({string.Join(", ", BrainstormSettings.Efforts)}).").ToProblem();
            }

            if (await store.GetAsync(id, ct).ConfigureAwait(false) is not { } idea)
            {
                return DomainError.NotFound($"Idea {id}").ToProblem();
            }

            await service.ChangeSettingsAsync(idea, model, effort, ct).ConfigureAwait(false);
            return TypedResults.NoContent();
        }).WithName("ChangeIdeaSettings").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static string Author(ClaimsPrincipal user) => user.Identity?.Name ?? "web";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <param name="Text">The idea, in your words.</param>
    /// <param name="Repo">The repository (optional when only one is registered).</param>
    /// <param name="Model">The brainstorm's model (fable, opus, sonnet or a full name); none = the default.</param>
    /// <param name="Effort">low, medium, high, xhigh or max; none = the default.</param>
    public sealed record StartIdeaRequest(string? Text, string? Repo, string? Model, string? Effort);

    /// <param name="Text">A message, or a choice once work items are proposed (✅ Create, 🚀 Create and start, ✏️ Change, 🗑 Discard).</param>
    public sealed record IdeaMessageRequest(string? Text);

    /// <param name="Model">From the next reply; none keeps the current one.</param>
    /// <param name="Effort">From the next reply; none keeps the current one.</param>
    public sealed record IdeaSettingsRequest(string? Model, string? Effort);
}
