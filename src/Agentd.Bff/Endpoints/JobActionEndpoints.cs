using System.Security.Claims;
using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Application.Queries;
using Agentd.Bff.Http;
using Agentd.Bff.ViewModels;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary>Job actions from the Web UI (cancel, retry, message, run) and the job diff. Unsafe methods only, under <c>/api</c>.</summary>
public static class JobActionEndpoints
{
    public const int MaxMessageLength = 8_000;

    public static RouteGroupBuilder MapJobActions(this RouteGroupBuilder api)
    {
        api.MapPost("/jobs/{id:long}/cancel", async (long id, ClaimsPrincipal user, [FromServices] ICommandHandler<CancelJob, Unit> handler, CancellationToken ct) =>
            (await handler.Handle(new CancelJob(new JobId(id), UserName(user)), ct).ConfigureAwait(false)).ToHttpResult(_ => TypedResults.NoContent()));

        api.MapPost("/jobs/{id:long}/retry", async (long id, [FromServices] ICommandHandler<RetryJob, int> handler, CancellationToken ct) =>
            (await handler.Handle(new RetryJob(new JobId(id)), ct).ConfigureAwait(false)).ToHttpResult(_ => TypedResults.NoContent()));

        api.MapPost("/jobs/{id:long}/messages", async (long id, MessageRequest? body, ClaimsPrincipal user, [FromServices] ICommandHandler<SubmitDeveloperMessage, DeveloperMessageOutcome> handler, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Text) || body.Text.Length > MaxMessageLength)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["text"] = [$"text must be 1 to {MaxMessageLength} characters."] });
            }

            // No provider (Via = null): the message came from the Web UI, so it is mirrored to every chat thread.
            var outcome = await handler.Handle(new SubmitDeveloperMessage(new JobId(id), body.Text.Trim(), UserName(user)), ct).ConfigureAwait(false);
            return outcome.ToHttpResult(o => o == DeveloperMessageOutcome.NotAccepted
                ? new DomainError("not_accepted", $"Job {id} doesn't take messages in its current state.").ToProblem()
                : TypedResults.Accepted((string?)null, new MessageAcceptedVm(o.ToString())));
        });

        api.MapPost("/workitems/{id:int}/run", async (int id, [FromServices] ICommandHandler<ClaimWorkItem, JobId> handler, CancellationToken ct) =>
            id <= 0
                ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["id"] = ["A work item id is positive."] })
                : (await handler.Handle(new ClaimWorkItem(WorkItemId.From(id), Force: true), ct).ConfigureAwait(false))
                    .ToHttpResult(job => TypedResults.Accepted($"/api/jobs/{job}", new RunAcceptedVm(job.Value))));

        api.MapGet("/jobs/{id:long}/diff", async (long id, [FromServices] IQueryHandler<GetJobDiff, Result<BranchDiff>> handler, CancellationToken ct) =>
            (await handler.Handle(new GetJobDiff(new JobId(id)), ct).ConfigureAwait(false)).ToHttpResult(diff => TypedResults.Ok(DiffVm.From(diff))));

        return api;
    }

    private static string UserName(ClaimsPrincipal user) => user.Identity?.Name ?? "web";

    public sealed record MessageRequest(string? Text);
}
