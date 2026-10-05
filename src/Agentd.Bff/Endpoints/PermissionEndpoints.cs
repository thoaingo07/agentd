using System.Security.Claims;
using Agentd.Application.Abstractions;
using Agentd.Application.Permissions;
using Agentd.Bff.Http;
using Agentd.Bff.ViewModels;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary>Answering the agent's permission requests from the Web UI, and the remembered approvals (Settings).</summary>
public static class PermissionEndpoints
{
    /// <summary>The Web UI's choices, the same as the chat's 1–4.</summary>
    public static readonly string[] Choices = ["once", "job", "repo", "deny"];

    public static RouteGroupBuilder MapPermissions(this RouteGroupBuilder api)
    {
        api.MapPost("/jobs/{id:long}/permissions/{requestId:long}", async (long id, long requestId, PermissionAnswerRequest? body, ClaimsPrincipal user,
            [FromServices] ICommandHandler<PermissionAnswer, bool> handler, CancellationToken ct) =>
        {
            if (body?.Choice is not { } choice || !Choices.Contains(choice, StringComparer.Ordinal))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["choice"] = [$"choice is one of {string.Join(", ", Choices)}."] });
            }

            var answered = await handler.Handle(new PermissionAnswer(new JobId(id), choice, user.Identity?.Name ?? "web", requestId), ct).ConfigureAwait(false);
            return answered.ToHttpResult(ok => ok
                ? TypedResults.NoContent()
                : new DomainError("already_decided", $"Permission request {requestId} was already answered or has expired.").ToProblem());
        }).WithName("AnswerPermission").Accepts<PermissionAnswerRequest>("application/json").Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/permissions/rules", async ([FromServices] IQueryHandler<GetPermissionRules, IReadOnlyList<PermissionRule>> handler, CancellationToken ct) =>
            TypedResults.Ok((await handler.Handle(new GetPermissionRules(), ct).ConfigureAwait(false))
                .Select(r => new PermissionRuleVm(r.Id, r.Repository, r.JobId?.Value, r.RuleKey, r.CreatedBy, r.CreatedAt)).ToList()))
            .WithName("GetPermissionRules");

        // Revoking changes what agents may do in every job of a repository, so it's for Admins (like !repo add/remove).
        api.MapDelete("/permissions/rules/{ruleId:long}", async (long ruleId, ClaimsPrincipal user, [FromServices] ICommandHandler<RevokePermissionRule, Unit> handler, CancellationToken ct) =>
            (await handler.Handle(new RevokePermissionRule(ruleId, user.Identity?.Name ?? "web"), ct).ConfigureAwait(false)).ToHttpResult(_ => TypedResults.NoContent()))
            .RequireAuthorization(p => p.RequireRole("Admin"))
            .WithName("RevokePermissionRule").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    public sealed record PermissionAnswerRequest(string Choice);
}
