using Agentd.Application.Abstractions;
using Agentd.Application.Queries;
using Agentd.Bff.ViewModels;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary>The work item view: one work item across all its jobs (UI §4.3a).</summary>
public static class WorkItemEndpoints
{
    public static RouteGroupBuilder MapWorkItems(this RouteGroupBuilder api)
    {
        api.MapGet("/workitems/{id:int}", async Task<IResult> (int id, [FromServices] IQueryHandler<GetWorkItem, WorkItemSummary?> handler, CancellationToken ct) =>
            id > 0 && await handler.Handle(new GetWorkItem(WorkItemId.From(id)), ct).ConfigureAwait(false) is { } summary
                ? TypedResults.Ok(WorkItemVm.From(summary))
                : TypedResults.Problem($"agentd has no jobs for work item {id}.", statusCode: StatusCodes.Status404NotFound, title: "Not found"))
            .WithName("GetWorkItem").Produces<WorkItemVm>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/workitems/{id:int}/timeline", async Task<IResult> (int id, long? after, long? before, int? limit, [FromServices] IQueryHandler<GetWorkItemTimeline, EventPage> handler, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (id <= 0)
            {
                errors["id"] = ["A work item id is positive."];
            }

            if (after is not null && before is not null)
            {
                errors["after"] = ["Use either after or before, not both."];
            }

            if (limit is < 1 or > JobEndpoints.MaxEventLimit)
            {
                errors["limit"] = [$"limit must be between 1 and {JobEndpoints.MaxEventLimit}."];
            }

            if (errors.Count > 0)
            {
                return TypedResults.ValidationProblem(errors);
            }

            var page = await handler.Handle(new GetWorkItemTimeline(WorkItemId.From(id), after, before, limit ?? JobEndpoints.DefaultEventLimit), ct).ConfigureAwait(false);
            return TypedResults.Ok(EventPageVm.From(page));
        }).WithName("GetWorkItemTimeline").Produces<EventPageVm>().ProducesValidationProblem();

        api.MapGet("/workitems/{id:int}/conversation", async Task<IResult> (int id, [FromServices] IQueryHandler<GetWorkItemConversation, IReadOnlyList<ConversationEntry>> handler, CancellationToken ct) =>
            id <= 0
                ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["id"] = ["A work item id is positive."] })
                : TypedResults.Ok((await handler.Handle(new GetWorkItemConversation(WorkItemId.From(id)), ct).ConfigureAwait(false)).Select(ConversationEntryVm.From).ToList()))
            .WithName("GetWorkItemConversation").Produces<List<ConversationEntryVm>>().ProducesValidationProblem();

        return api;
    }
}
