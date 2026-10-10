using System.Globalization;
using System.Security.Claims;
using Agentd.Application.Monitor;
using Agentd.Bff.Http;
using Agentd.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary>
/// <c>/api/watches</c>: the PR Monitor's watch list from the Web UI, like <c>!watch</c> / <c>!unwatch</c> and the 👀 thread's
/// <b>push</b> / <b>discard</b> (docs/architect/pr-reviewer-and-monitor.md §2.0).
/// </summary>
public static class WatchEndpoints
{
    /// <summary>What a watch can be told from the Web UI: the 👀 thread's answers.</summary>
    public static readonly string[] Actions = ["push", "discard", "unwatch"];

    public static RouteGroupBuilder MapWatches(this RouteGroupBuilder api)
    {
        var watches = api.MapGroup("/watches");

        watches.MapGet("/", async ([FromServices] IPrWatchStore store, CancellationToken ct) =>
            TypedResults.Ok((await store.ListActiveAsync(ct).ConfigureAwait(false)).Select(WatchVm.From).ToList()))
            .WithName("ListWatches");

        watches.MapGet("/{id:long}", async Task<IResult> (long id, [FromServices] IPrWatchStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct).ConfigureAwait(false) is { } watch
                ? TypedResults.Ok(new WatchDetailVm(WatchVm.From(watch),
                    [.. (await store.ListMessagesAsync(id, ct).ConfigureAwait(false)).Select(m => new WatchMessageVm(m.Text, m.At))]))
                : DomainError.NotFound($"Watch {id}").ToProblem())
            .WithName("GetWatch").Produces<WatchDetailVm>().ProducesProblem(StatusCodes.Status404NotFound);

        watches.MapPost("/", async (WatchRequest? body, ClaimsPrincipal user, [FromServices] PrWatchService service, CancellationToken ct) =>
        {
            if (body?.PullRequestId is not > 0)
            {
                return DomainError.Validation("Which PR? Give its number.").ToProblem();
            }

            List<string> args = [body.PullRequestId.Value.ToString(CultureInfo.InvariantCulture)];
            if (!string.IsNullOrWhiteSpace(body.Repo))
            {
                args.AddRange(["--repo", body.Repo.Trim()]);
            }

            return (await service.WatchAsync(PrWatchService.Web, Author(user), args, ct).ConfigureAwait(false)).ToHttpResult(m => TypedResults.Ok(new WatchReplyVm(m)));
        }).WithName("WatchPullRequest").Produces<WatchReplyVm>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);

        watches.MapPost("/{id:long}/{action}", async Task<IResult> (long id, string action, ClaimsPrincipal user, [FromServices] PrWatchService service, [FromServices] IPrWatchStore store,
            CancellationToken ct) =>
        {
            if (!Actions.Contains(action))
            {
                return DomainError.Validation($"Use one of: {string.Join(", ", Actions)}.").ToProblem();
            }

            if (await store.GetAsync(id, ct).ConfigureAwait(false) is not { } watch)
            {
                return DomainError.NotFound($"Watch {id}").ToProblem();
            }

            if (action != "unwatch" && watch.Pending is null)
            {
                return DomainError.Conflict($"No fix waits for PR !{watch.PullRequestId}.").ToProblem();
            }

            // The same answers as in the 👀 thread (unwatch also drops a waiting fix).
            return TypedResults.Ok(new WatchReplyVm(await service.HandleThreadMessageAsync(watch, action, Author(user), ct).ConfigureAwait(false)));
        }).WithName("AnswerWatch").Produces<WatchReplyVm>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static string Author(ClaimsPrincipal user) => user.Identity?.Name ?? "web";

    /// <param name="Repo">The registered repository (optional when only one is registered).</param>
    /// <param name="PullRequestId">The PR to watch.</param>
    public sealed record WatchRequest(string? Repo, int? PullRequestId);
}

/// <summary>A fix the monitor prepared, waiting for push or discard.</summary>
public sealed record PendingFixVm(string Commit, string Summary, DateTimeOffset ExpiresAt);

/// <summary>A watched PR; <c>inChat</c>: it has a 👀 thread too.</summary>
public sealed record WatchVm(long Id, string Repo, int PullRequestId, string Title, string WatchedBy, bool Active, int FixRounds, PendingFixVm? Pending, bool InChat,
    DateTimeOffset CreatedAt)
{
    public static WatchVm From(PrWatch w)
    {
        ArgumentNullException.ThrowIfNull(w);
        return new(w.Id, w.Repository, w.PullRequestId, w.Title, w.WatchedBy, w.Active, w.FixRounds,
            w.Pending is { } p ? new PendingFixVm(p.Commit, p.Summary, p.ExpiresAt) : null, w.ThreadId is not null, w.CreatedAt);
    }
}

/// <summary>A notice from the monitor (what the 👀 thread shows).</summary>
public sealed record WatchMessageVm(string Text, DateTimeOffset At);

/// <summary>A watch and everything the monitor said about it.</summary>
public sealed record WatchDetailVm(WatchVm Watch, IReadOnlyList<WatchMessageVm> Messages);

/// <summary>What agentd answered (as it would in chat).</summary>
public sealed record WatchReplyVm(string Message);
