using Agentd.Application.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Claude;

/// <summary>
/// Puts each turn's prompt in the prompt log (docs/architect/prompt-log.md) as it's sent, and how it ended. Never fails a
/// turn: a log that can't be written is logged and skipped.
/// </summary>
public sealed partial class PromptRecorder(IPromptLog? log = null, IOptions<PromptLogOptions>? options = null, ILogger<PromptRecorder>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<PromptRecorder>.Instance;

    private PromptLogOptions Options => options?.Value ?? new PromptLogOptions();

    /// <summary>The prompt's id in the log, or null when it isn't kept.</summary>
    public async Task<long?> StartAsync(PromptStart prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (log is null || !Options.Enabled)
        {
            return null;
        }

        try
        {
            return await log.StartAsync(prompt with { Prompt = Cut(prompt.Prompt)! }, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the log is for learning: a turn never fails because of it
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(_logger, ex, prompt.Source, prompt.SourceId);
            return null;
        }
    }

    /// <summary>A thread turn's transcript (its events in order), kept with its prompt; at most <see cref="PromptLogOptions.MaxEvents"/>.</summary>
    public async Task AddEventsAsync(long? id, IReadOnlyList<PromptEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (log is null || id is not { } prompt || events.Count == 0)
        {
            return;
        }

        var kept = events.Count <= Options.MaxEvents ? events
            : [.. events.Take(Options.MaxEvents), new PromptEvent("agent.text", $"{{\"text\":\"… {events.Count - Options.MaxEvents} more events not kept (Agentd:PromptLog:MaxEvents)\"}}", events[^1].At)];
        try
        {
            await log.AddEventsAsync(prompt, kept, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the log is for learning: a turn never fails because of it
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFinishFailed(_logger, ex, prompt);
        }
    }

    public async Task FinishAsync(long? id, string outcome, string? error = null, string? reply = null)
    {
        if (log is null || id is not { } prompt)
        {
            return;
        }

        try
        {
            await log.FinishAsync(prompt, outcome, Cut(error), Cut(reply), CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the log is for learning: a turn never fails because of it
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFinishFailed(_logger, ex, prompt);
        }
    }

    private string? Cut(string? text) =>
        text is null || text.Length <= Options.MaxChars ? text : text[..Options.MaxChars] + "\n… (cut: longer than Agentd:PromptLog:MaxChars)";

    [LoggerMessage(Level = LogLevel.Warning, Message = "The prompt log couldn't record a {Source} {SourceId} prompt")]
    private static partial void LogFailed(ILogger logger, Exception exception, string source, long sourceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The prompt log couldn't record how prompt {Id} ended")]
    private static partial void LogFinishFailed(ILogger logger, Exception exception, long id);
}
