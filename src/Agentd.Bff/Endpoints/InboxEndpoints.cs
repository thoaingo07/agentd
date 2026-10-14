using System.Security.Claims;
using Agentd.Application.Chats;
using Agentd.Application.Ideas;
using Agentd.Application.Inbox;
using Agentd.Application.Jobs;
using Agentd.Application.Reviews;
using Agentd.Bff.Http;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Endpoints;

/// <summary>
/// <c>/api/inbox</c> (docs/architect/inbox.md): agentd's messages to people, one thread per job, idea, review, chat or
/// watched PR, waiting on you first. A reply goes to the source as its page or chat thread would take it.
/// </summary>
public static class InboxEndpoints
{
    public const int MaxThreads = 500;

    private static readonly string[] s_sources = [InboxSources.Job, InboxSources.Idea, InboxSources.Review, InboxSources.Chat, InboxSources.Watch, InboxSources.Incident];

    public static RouteGroupBuilder MapInbox(this RouteGroupBuilder api)
    {
        var inbox = api.MapGroup("/inbox");

        inbox.MapGet("/", async Task<IResult> (string? view, int? limit, ClaimsPrincipal user, [FromServices] IInbox store, CancellationToken ct) =>
        {
            var me = Name(user);
            var threads = await store.ThreadsAsync(me, view == "mine" ? me : null, Math.Clamp(limit ?? 100, 1, MaxThreads), ct).ConfigureAwait(false);
            var counts = await store.CountsAsync(me, ct).ConfigureAwait(false);
            return TypedResults.Ok(new InboxPageVm([.. threads.Select(InboxThreadVm.From)], new InboxCountsVm(counts.Waiting, counts.Unread)));
        }).WithName("GetInbox").Produces<InboxPageVm>();

        inbox.MapGet("/counts", async (ClaimsPrincipal user, [FromServices] IInbox store, CancellationToken ct) =>
            await store.CountsAsync(Name(user), ct).ConfigureAwait(false) is var c ? TypedResults.Ok(new InboxCountsVm(c.Waiting, c.Unread)) : null)
            .WithName("GetInboxCounts").Produces<InboxCountsVm>();

        inbox.MapGet("/{source}/{sourceId:long}", async Task<IResult> (string source, long sourceId, long? before, [FromServices] IInbox store, CancellationToken ct) =>
            !s_sources.Contains(source)
                ? Bad()
                : TypedResults.Ok((await store.ThreadAsync(source, sourceId, before, 100, ct).ConfigureAwait(false)).Select(InboxItemVm.From).ToList()))
            .WithName("GetInboxThread").Produces<List<InboxItemVm>>().ProducesProblem(StatusCodes.Status400BadRequest);

        inbox.MapPost("/{source}/{sourceId:long}/read", async Task<IResult> (string source, long sourceId, InboxReadRequest? body, ClaimsPrincipal user,
            [FromServices] IInbox store, CancellationToken ct) =>
        {
            if (!s_sources.Contains(source) || body?.Until is not > 0)
            {
                return Bad();
            }

            await store.MarkReadAsync(Name(user), source, sourceId, body.Until.Value, ct).ConfigureAwait(false);
            return TypedResults.NoContent();
        }).WithName("MarkInboxRead").Accepts<InboxReadRequest>("application/json").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status400BadRequest);

        inbox.MapPost("/{source}/{sourceId:long}/reply", async Task<IResult> (string source, long sourceId, InboxReplyRequest? body, ClaimsPrincipal user,
            HttpContext http, CancellationToken ct) =>
        {
            if (!s_sources.Contains(source))
            {
                return Bad();
            }

            if (string.IsNullOrWhiteSpace(body?.Text) || body.Text.Length > JobActionEndpoints.MaxMessageLength)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["text"] = [$"text must be 1 to {JobActionEndpoints.MaxMessageLength} characters."] });
            }

            var result = await ReplyAsync(http.RequestServices, source, sourceId, body.Text.Trim(), Name(user), ct).ConfigureAwait(false);
            return result.IsSuccess ? TypedResults.Accepted((string?)null, new InboxReplyVm(result.Value!)) : result.Error.ToProblem();
        }).WithName("ReplyInInbox").Accepts<InboxReplyRequest>("application/json").Produces<InboxReplyVm>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    /// <summary>The reply, to its source: a job like its page's message box, an idea, chat or review like a message in its thread. A watched PR's old thread stays readable but takes no answers.</summary>
    private static async Task<Result<string>> ReplyAsync(IServiceProvider services, string source, long id, string text, string author, CancellationToken ct)
    {
        var finished = DomainError.Conflict($"This {source} doesn't take messages any more.");
        switch (source)
        {
            case InboxSources.Job:
                var outcome = await services.GetRequiredService<JobMessages>().RouteAsync(new JobId(id), text, author, null, ct).ConfigureAwait(false);
                return !outcome.IsSuccess ? outcome.Error : outcome.Value == JobMessageOutcomes.NotAccepted ? finished : outcome.Value!;

            case InboxSources.Idea when services.GetService<IdeaService>() is { } ideas && services.GetService<IIdeaStore>() is { } store:
                return await store.GetAsync(id, ct).ConfigureAwait(false) is not { } idea ? DomainError.NotFound($"Idea {id}")
                    : await ideas.HandleMessageAsync(idea, author, text, ct, fromWeb: true).ConfigureAwait(false) ? "sent" : finished;

            case InboxSources.Chat when services.GetService<ChatService>() is { } chats && services.GetService<IChatStore>() is { } store:
                return await store.GetAsync(id, ct).ConfigureAwait(false) is not { } chat ? DomainError.NotFound($"Chat {id}")
                    : await chats.HandleMessageAsync(chat, author, text, ct, fromWeb: true).ConfigureAwait(false) ? "sent" : finished;

            case InboxSources.Review when services.GetService<ReviewService>() is { } reviews && services.GetService<IReviewStore>() is { } store:
                return await store.GetAsync(id, ct).ConfigureAwait(false) is not { } review ? DomainError.NotFound($"Review {id}")
                    : await reviews.HandleMessageAsync(review, author, text, ct).ConfigureAwait(false) ? "sent" : finished;

            case InboxSources.Watch:
                return DomainError.Conflict("Watching PRs is turned off for now.");

            case InboxSources.Incident:
                return DomainError.Conflict("Incidents don't take answers yet: investigating and Fix it come in the next version.");

            default:
                return DomainError.NotFound($"{source} {id}");
        }
    }

    private static IResult Bad() => DomainError.Validation($"source is one of {string.Join(", ", s_sources)}; until is a message id.").ToProblem();

    private static string Name(ClaimsPrincipal user) => user.Identity?.Name ?? "web";

    /// <param name="Until">The newest message read.</param>
    public sealed record InboxReadRequest(long? Until);

    /// <param name="Text">The answer: a question's option, or anything you'd write in the source's chat thread or page.</param>
    public sealed record InboxReplyRequest(string? Text);
}

/// <summary>The Inbox: its threads (waiting on you first, then unread, then the rest) and the nav's counts.</summary>
public sealed record InboxPageVm(IReadOnlyList<InboxThreadVm> Threads, InboxCountsVm Counts);

/// <summary>How many threads wait on you, and how many have messages you haven't read.</summary>
public sealed record InboxCountsVm(int Waiting, int Unread);

/// <summary>A reply's outcome, as the source answered it.</summary>
public sealed record InboxReplyVm(string Outcome);

/// <summary>A button of a question: send its <c>label</c> as the reply.</summary>
public sealed record InboxOptionVm(string Id, string Label);

/// <summary>One of agentd's messages in a thread (Markdown).</summary>
public sealed record InboxItemVm(long Id, string Source, long SourceId, string Title, string? Recipient, string Kind, string Markdown, IReadOnlyList<InboxOptionVm> Options,
    DateTimeOffset CreatedAt)
{
    public static InboxItemVm From(InboxItem i)
    {
        ArgumentNullException.ThrowIfNull(i);
        return new(i.Id, i.Source, i.SourceId, i.Title, i.Recipient, i.Kind, i.Markdown, [.. i.Options.Select(o => new InboxOptionVm(o.Id, o.Label))], i.CreatedAt);
    }
}

/// <summary>A thread in the list; <c>question</c> is its latest question while it waits on someone.</summary>
public sealed record InboxThreadVm(string Source, long SourceId, string Title, string? Recipient, long LatestId, string LatestKind, string LatestPreview, DateTimeOffset LatestAt,
    int Items, int Unread, bool Waiting, InboxItemVm? Question)
{
    public static InboxThreadVm From(InboxThread t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new(t.Source, t.SourceId, t.Title, t.Recipient, t.LatestId, t.LatestKind, t.LatestPreview, t.LatestAt, t.Items, t.Unread, t.Waiting,
            t.Question is null ? null : InboxItemVm.From(t.Question));
    }
}
