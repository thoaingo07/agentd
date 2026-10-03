using Agentd.Application.Abstractions;
using Agentd.Application.Queries;
using Agentd.Bff.ViewModels;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary>Read endpoints for the dashboard, job pages and history: bind → query → view model.</summary>
public static class JobEndpoints
{
    public const int DefaultEventLimit = 200;
    public const int MaxEventLimit = 500;
    public const int MaxPageSize = 100;

    public static RouteGroupBuilder MapJobReads(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async ([FromServices] IQueryHandler<GetDashboard, Dashboard> handler, CancellationToken ct) =>
            TypedResults.Ok(DashboardVm.From(await handler.Handle(new GetDashboard(), ct).ConfigureAwait(false))));

        api.MapGet("/jobs/{id:long}", async Task<IResult> (long id, [FromServices] IQueryHandler<GetJob, JobDetail?> handler, CancellationToken ct) =>
            await handler.Handle(new GetJob(new JobId(id)), ct).ConfigureAwait(false) is { } detail
                ? TypedResults.Ok(JobDetailVm.From(detail))
                : NotFound($"Job {id} was not found."));

        api.MapGet("/jobs/{id:long}/events", async Task<IResult> (long id, long? after, long? before, int? limit, [FromServices] IQueryHandler<GetJobEvents, EventPage> handler, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (after is not null && before is not null)
            {
                errors["after"] = ["Use either after or before, not both."];
            }

            if (limit is < 1 or > MaxEventLimit)
            {
                errors["limit"] = [$"limit must be between 1 and {MaxEventLimit}."];
            }

            if (errors.Count > 0)
            {
                return TypedResults.ValidationProblem(errors);
            }

            var page = await handler.Handle(new GetJobEvents(new JobId(id), after, before, limit ?? DefaultEventLimit), ct).ConfigureAwait(false);
            return TypedResults.Ok(EventPageVm.From(page));
        });

        api.MapGet("/history", async Task<IResult> (string? state, string? repo, string? q, int? page, int? pageSize, [FromServices] IQueryHandler<SearchHistory, HistoryPage> handler, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            List<JobState>? states = null;
            if (!string.IsNullOrWhiteSpace(state))
            {
                states = [];
                foreach (var name in state.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Enum.TryParse<JobState>(name, ignoreCase: true, out var parsed))
                    {
                        states.Add(parsed);
                    }
                    else
                    {
                        errors["state"] = [$"Unknown state '{name}'."];
                    }
                }
            }

            if (page is < 1)
            {
                errors["page"] = ["page starts at 1."];
            }

            if (pageSize is < 1 or > MaxPageSize)
            {
                errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
            }

            if (errors.Count > 0)
            {
                return TypedResults.ValidationProblem(errors);
            }

            var result = await handler.Handle(new SearchHistory(states, repo, q, page ?? 1, pageSize ?? 25), ct).ConfigureAwait(false);
            return TypedResults.Ok(HistoryPageVm.From(result));
        });

        return api;
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult NotFound(string detail) => TypedResults.Problem(detail, statusCode: StatusCodes.Status404NotFound, title: "Not found");
}
