using Agentd.Application.Prompts;
using Agentd.Bff.Http;
using Agentd.Bff.ViewModels;
using Agentd.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary>
/// <c>GET /api/prompts</c> and <c>/api/prompts/{id}</c>: every prompt agentd sent to an agent (docs/architect/prompt-log.md),
/// Admins only: prompts carry work item, PR and code text from every user's work.
/// </summary>
public static class PromptEndpoints
{
    public const int MaxPage = 100;

    private static readonly string[] s_sources =
    [
        PromptSources.Job, PromptSources.Idea, PromptSources.Chat, PromptSources.Review, PromptSources.ReviewSession,
        PromptSources.ReviewAsk, PromptSources.ReviewFix, PromptSources.FollowUp,
    ];

    private static readonly string[] s_outcomes =
    [
        PromptFilter.Running, PromptOutcomes.Ok, PromptOutcomes.Error, PromptOutcomes.UsageLimit, PromptOutcomes.Timeout, PromptOutcomes.Cancelled,
    ];

    public static RouteGroupBuilder MapPrompts(this RouteGroupBuilder api)
    {
        var prompts = api.MapGroup("/prompts").RequireAuthorization(p => p.RequireRole("Admin"));

        prompts.MapGet("/", async Task<IResult> (string? source, long? sourceId, string? step, string? model, string? outcome, DateTimeOffset? from, long? before, int? limit,
            [FromServices] IPromptLog log, CancellationToken ct) =>
        {
            if ((Blank(source) is { } s && !s_sources.Contains(s)) || (Blank(outcome) is { } o && !s_outcomes.Contains(o)))
            {
                return DomainError.Validation($"source is one of {string.Join(", ", s_sources)}; outcome one of {string.Join(", ", s_outcomes)}.").ToProblem();
            }

            var size = Math.Clamp(limit ?? 50, 1, MaxPage);
            var rows = await log.ListAsync(new PromptFilter(Blank(source), sourceId, Blank(step), Blank(model), Blank(outcome), from), before, size + 1, ct).ConfigureAwait(false);
            return TypedResults.Ok(new PromptPageVm([.. rows.Take(size).Select(PromptSummaryVm.From)], rows.Count > size));
        }).WithName("ListPrompts").Produces<PromptPageVm>().ProducesProblem(StatusCodes.Status400BadRequest);

        prompts.MapGet("/{id:long}", async Task<IResult> (long id, [FromServices] IPromptLog log, CancellationToken ct) =>
            await log.GetAsync(id, ct).ConfigureAwait(false) is { } p
                ? TypedResults.Ok(PromptDetailVm.From(p))
                : DomainError.NotFound($"Prompt {id}").ToProblem())
            .WithName("GetPrompt").Produces<PromptDetailVm>().ProducesProblem(StatusCodes.Status404NotFound);

        // The turn's transcript (text, tool calls and results), in the job Transcript's event shape.
        prompts.MapGet("/{id:long}/transcript", async Task<IResult> (long id, [FromServices] IPromptLog log, CancellationToken ct) =>
        {
            if (await log.GetAsync(id, ct).ConfigureAwait(false) is not { } prompt)
            {
                return DomainError.NotFound($"Prompt {id}").ToProblem();
            }

            var events = await log.TranscriptAsync(id, MaxTranscript, ct).ConfigureAwait(false);
            return TypedResults.Ok(events.Select((e, i) => new EventVm(i + 1, prompt.Source == PromptSources.Job ? prompt.SourceId : null, e.At, e.Type, Payload(e.PayloadJson))).ToList());
        }).WithName("GetPromptTranscript").Produces<List<EventVm>>().ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    /// <summary>The most events a transcript returns.</summary>
    public const int MaxTranscript = 5000;

    private static System.Text.Json.JsonElement Payload(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return doc.RootElement.Clone();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A prompt in the list; <c>preview</c> is its first 300 characters, <c>outcome</c> null while it runs.</summary>
public sealed record PromptSummaryVm(
    long Id, string Source, long SourceId, string? Step, Guid? Session, bool Resume, string? Model, string? Effort, string? Profile,
    string? Outcome, string? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Preview, int PromptChars, int ReplyChars, bool HasRules)
{
    public static PromptSummaryVm From(PromptSummary p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.Id, p.Source, p.SourceId, p.Step, p.Session, p.Resume, p.Model, p.Effort, p.Profile, p.Outcome, p.Error, p.StartedAt, p.FinishedAt,
            p.Preview, p.PromptChars, p.ReplyChars, p.HasRules);
    }
}

/// <summary>A page, newest first; <c>hasMore</c>: older prompts exist (ask again with <c>before</c> = the last id).</summary>
public sealed record PromptPageVm(IReadOnlyList<PromptSummaryVm> Prompts, bool HasMore);

/// <summary>A prompt, whole: what was sent, the system rules with it, and the reply.</summary>
public sealed record PromptDetailVm(
    long Id, string Source, long SourceId, string? Step, Guid? Session, bool Resume, string? Model, string? Effort, string? Profile,
    string? Outcome, string? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Prompt, string? Reply, string? Rules)
{
    public static PromptDetailVm From(PromptDetail p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.Id, p.Source, p.SourceId, p.Step, p.Session, p.Resume, p.Model, p.Effort, p.Profile, p.Outcome, p.Error, p.StartedAt, p.FinishedAt,
            p.Prompt, p.Reply, p.Rules);
    }
}
