using Agentd.Application.Ideas;

namespace Agentd.Application.Prompts;

/// <summary>What a prompt was for (docs/architect/prompt-log.md).</summary>
public static class PromptSources
{
    public const string Job = "job";
    public const string Idea = "idea";
    public const string Chat = "chat";
    public const string Review = "review";
    public const string ReviewSession = "review-session";
    public const string ReviewAsk = "review-ask";
    public const string ReviewFix = "review-fix";
    public const string FollowUp = "follow-up";

    /// <summary>The source of a thread turn; its id is the turn's <see cref="BrainstormTurn.IdeaId"/> (the idea, chat, review, session, …).</summary>
    public static string For(ThreadTurnKind kind) => kind switch
    {
        ThreadTurnKind.Review => Review,
        ThreadTurnKind.ReviewSession => ReviewSession,
        ThreadTurnKind.ReviewAsk => ReviewAsk,
        ThreadTurnKind.ReviewFix => ReviewFix,
        ThreadTurnKind.FollowUp => FollowUp,
        ThreadTurnKind.Chat => Chat,
        _ => Idea,
    };
}

/// <summary>How a prompt's turn ended.</summary>
public static class PromptOutcomes
{
    public const string Ok = "ok";
    public const string Error = "error";
    public const string UsageLimit = "usage-limit";
    public const string Timeout = "timeout";
    public const string Cancelled = "cancelled";
}

/// <param name="Source">One of <see cref="PromptSources"/>.</param>
/// <param name="SourceId">The job, idea, chat, review, review session or ask thread.</param>
/// <param name="Step">A job's cycle step (implement, review, …).</param>
/// <param name="Session">The agent's session.</param>
/// <param name="Resume">Whether it continued that session.</param>
/// <param name="Model">The model asked for (none: the runner's default).</param>
/// <param name="Effort">The effort asked for.</param>
/// <param name="Profile">The subscription or provider profile.</param>
/// <param name="Rules">The system rules appended to the agent's own (stored once per distinct text).</param>
/// <param name="Prompt">What was sent.</param>
public sealed record PromptStart(
    string Source, long SourceId, string? Step, Guid? Session, bool Resume, string? Model, string? Effort, string? Profile, string? Rules, string Prompt);

/// <summary>What the Prompts page filters by; null is any. <see cref="Outcome"/> may also be <see cref="PromptFilter.Running"/>.</summary>
public sealed record PromptFilter(string? Source = null, long? SourceId = null, string? Step = null, string? Model = null, string? Outcome = null, DateTimeOffset? From = null)
{
    /// <summary>Not finished yet.</summary>
    public const string Running = "running";
}

/// <summary>A prompt in the list: its settings, how it ended, the start of what was sent and how long it and the reply were.</summary>
public sealed record PromptSummary(
    long Id, string Source, long SourceId, string? Step, Guid? Session, bool Resume, string? Model, string? Effort, string? Profile,
    string? Outcome, string? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Preview, int PromptChars, int ReplyChars, bool HasRules);

/// <summary>
/// A conversation in the prompt log: every turn sent for one source (a job, idea, chat, review, …), how many, when, the
/// latest turn's outcome, whether one still runs, and the models used. <see cref="Preview"/> is the first prompt's start.
/// </summary>
public sealed record PromptConversation(
    string Source, long SourceId, int Turns, DateTimeOffset FirstAt, DateTimeOffset LastAt, long LastId, string? LastOutcome, bool Running,
    IReadOnlyList<string> Models, string Preview);

/// <summary>A prompt, whole: what was sent, the system rules with it, and the reply.</summary>
public sealed record PromptDetail(
    long Id, string Source, long SourceId, string? Step, Guid? Session, bool Resume, string? Model, string? Effort, string? Profile,
    string? Outcome, string? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Prompt, string? Reply, string? Rules);

/// <summary>
/// Every prompt agentd sends to an agent, with its system rules, settings and how it ended, so the team can learn from them
/// (docs/architect/prompt-log.md). Recording never fails a turn.
/// </summary>
public interface IPromptLog
{
    /// <summary>Records a prompt as it's sent; returns its id.</summary>
    Task<long> StartAsync(PromptStart prompt, CancellationToken cancellationToken);

    /// <param name="id">The prompt.</param>
    /// <param name="outcome">One of <see cref="PromptOutcomes"/>.</param>
    /// <param name="failure">Why it failed, if it did.</param>
    /// <param name="reply">The agent's final answer (thread turns; a job's transcript is on its page).</param>
    /// <param name="cancellationToken">Cancels it.</param>
    Task FinishAsync(long id, string outcome, string? failure, string? reply, CancellationToken cancellationToken);

    /// <summary>A page of prompts, newest first (the Prompts page).</summary>
    Task<IReadOnlyList<PromptSummary>> ListAsync(PromptFilter filter, long? before, int limit, CancellationToken cancellationToken);

    /// <summary>One prompt, whole, or null.</summary>
    Task<PromptDetail?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Deletes prompts sent before <paramref name="before"/> (and rules no prompt uses any more); returns how many.</summary>
    Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken);

    /// <summary>Conversations (one per source), the latest activity first, below <paramref name="before"/> (a conversation's latest prompt id).</summary>
    Task<IReadOnlyList<PromptConversation>> ConversationsAsync(string? source, long? before, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PromptConversation>>([]);

    /// <summary>One conversation's turns, oldest first, whole.</summary>
    Task<IReadOnlyList<PromptDetail>> ConversationAsync(string source, long sourceId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PromptDetail>>([]);

    /// <summary>A thread turn's transcript, written once as it ends (a job's events are in the event log already).</summary>
    Task AddEventsAsync(long id, IReadOnlyList<PromptEvent> events, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A turn's transcript in order: a thread turn's own events, or its job's agent events while it ran.</summary>
    Task<IReadOnlyList<PromptEvent>> TranscriptAsync(long id, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PromptEvent>>([]);
}

/// <summary>One event of an agent's run (<c>agent.text</c>, <c>agent.tool_call</c>, …) with its JSON payload, as a job's events.</summary>
public sealed record PromptEvent(string Type, string PayloadJson, DateTimeOffset At);

/// <summary>Configuration section <c>Agentd:PromptLog</c>.</summary>
public sealed class PromptLogOptions
{
    public const string Section = "Agentd:PromptLog";

    /// <summary>Record prompts at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Days prompts are kept (the hourly sweep deletes older ones); 0 keeps them forever.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Longer prompts and replies are cut to this many characters.</summary>
    public int MaxChars { get; set; } = 200_000;

    /// <summary>A thread turn's transcript keeps at most this many events (the last ones are dropped, saying so).</summary>
    public int MaxEvents { get; set; } = 5000;
}
