using System.Security.Claims;
using Agentd.Application.Chats;
using Agentd.Bff.Http;
using Agentd.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary>
/// <c>/api/chats</c>: code questions (<c>!chat</c>) from the Web UI. A message from the page is handled like one in the
/// chat's thread; a chat started here has no thread.
/// </summary>
public static class ChatEndpoints
{
    public const int ListLimit = 100;

    public static RouteGroupBuilder MapChats(this RouteGroupBuilder api)
    {
        var chats = api.MapGroup("/chats");

        chats.MapGet("/", async ([FromServices] IChatStore store, CancellationToken ct) =>
            TypedResults.Ok((await store.ListAsync(ListLimit, ct).ConfigureAwait(false))
                .Select(c => new ChatSummaryVm(c.Id, c.Author, c.Status, c.Repositories, c.Model, c.Effort, c.InChat, c.Title, c.Messages, c.CreatedAt, c.UpdatedAt)).ToList()))
            .WithName("ListChats");

        chats.MapGet("/{id:long}", async Task<IResult> (long id, [FromServices] IChatStore store, [FromServices] ChatService? service, CancellationToken ct) =>
            await store.GetAsync(id, ct).ConfigureAwait(false) is { } chat
                ? TypedResults.Ok(new ChatDetailVm(chat.Id, chat.Author, chat.Status, chat.Repositories, chat.Model, chat.Effort, chat.ThreadId is not null,
                    [.. (await store.ListMessagesAsync(id, ct).ConfigureAwait(false)).Select(m => new ChatMessageVm(m.Direction, m.Author, m.Text, m.At))],
                    service?.IsThinking(id) ?? false))
                : DomainError.NotFound($"Chat {id}").ToProblem())
            .WithName("GetChat").Produces<ChatDetailVm>().ProducesProblem(StatusCodes.Status404NotFound);

        chats.MapPost("/", async (StartChatRequest? body, ClaimsPrincipal user, [FromServices] ChatService service, CancellationToken ct) =>
            (await service.StartOnWebAsync(Author(user), body?.Text ?? string.Empty, Blank(body?.Repo), Blank(body?.Model), Blank(body?.Effort), ct).ConfigureAwait(false))
                .ToHttpResult(id => TypedResults.Created($"/api/chats/{id}", new ChatStartedVm(id))))
            .WithName("StartChat").Produces<ChatStartedVm>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest);

        chats.MapPost("/{id:long}/messages", async Task<IResult> (long id, ChatMessageRequest? body, ClaimsPrincipal user, [FromServices] ChatService service, [FromServices] IChatStore store,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Text) || body.Text.Length > JobActionEndpoints.MaxMessageLength)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["text"] = [$"text must be 1 to {JobActionEndpoints.MaxMessageLength} characters."] });
            }

            return await SendAsync(id, body.Text.Trim(), user, service, store, ct).ConfigureAwait(false);
        }).WithName("SendChatMessage").Produces(StatusCodes.Status202Accepted).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        chats.MapPost("/{id:long}/close", (long id, ClaimsPrincipal user, [FromServices] ChatService service, [FromServices] IChatStore store, CancellationToken ct) =>
            SendAsync(id, "close", user, service, store, ct))
            .WithName("CloseChat").Produces(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    /// <summary>Like a message in the chat's thread ("close" closes it).</summary>
    private static async Task<IResult> SendAsync(long id, string text, ClaimsPrincipal user, ChatService service, IChatStore store, CancellationToken ct) =>
        await store.GetAsync(id, ct).ConfigureAwait(false) is not { } chat
            ? DomainError.NotFound($"Chat {id}").ToProblem()
            : await service.HandleMessageAsync(chat, Author(user), text, ct, fromWeb: true).ConfigureAwait(false)
                ? TypedResults.Accepted($"/api/chats/{id}")
                : DomainError.Conflict($"Chat {id} is closed; ask a new question.").ToProblem();

    private static string Author(ClaimsPrincipal user) => user.Identity?.Name ?? "web";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <param name="Text">The question.</param>
    /// <param name="Repo">Only this repository (none: every registered one).</param>
    /// <param name="Model">fable, opus, sonnet or a full name; none = the chat step's default.</param>
    /// <param name="Effort">low, medium, high, xhigh or max.</param>
    public sealed record StartChatRequest(string? Text, string? Repo, string? Model, string? Effort);

    /// <param name="Text">A follow-up ("close" closes the chat).</param>
    public sealed record ChatMessageRequest(string? Text);
}

/// <summary>A chat: its first question is the title; <c>inChat</c>: it also has a chat thread.</summary>
public sealed record ChatSummaryVm(long Id, string Author, string Status, IReadOnlyList<string> Repos, string? Model, string? Effort, bool InChat, string Title, int Messages,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>One message: <c>in</c> from a person; <c>out</c> from the agent (author <c>agent</c>, Markdown) or agentd (<c>agentd</c>).</summary>
public sealed record ChatMessageVm(string Direction, string Author, string Text, DateTimeOffset At);

/// <summary>A chat and its whole conversation; <c>thinking</c>: the agent is writing an answer.</summary>
public sealed record ChatDetailVm(long Id, string Author, string Status, IReadOnlyList<string> Repos, string? Model, string? Effort, bool InChat,
    IReadOnlyList<ChatMessageVm> Messages, bool Thinking);

/// <summary>A new chat: its page is <c>/chats/{id}</c>.</summary>
public sealed record ChatStartedVm(long Id);
