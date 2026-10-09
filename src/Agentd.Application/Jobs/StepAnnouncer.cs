using System.Text.Json;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Jobs;

/// <summary>
/// "🔨 Implement · deepseek · deepseek-flash": which step a job's agent turn runs, on which provider, model and effort.
/// Posted in the job's thread and logged as a <c>step.started</c> event (the session page's timeline) when a step starts,
/// not when the same step resumes (an answer, a restart). The model is the one Claude Code reports at start-up.
/// </summary>
public sealed class StepAnnouncer(IJobRepository jobs, IOutbox outbox, IEventStore events, IOptions<ModelsOptions>? models = null) : IStepAnnouncer
{
    public const string EventType = "step.started";

    private readonly Dictionary<long, string> _last = [];
    private readonly Lock _gate = new();

    public async Task AnnounceAsync(AgentRunRequest turn, string? model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var step = turn.Step ?? JobSteps.Implement;
        var round = step == JobSteps.Fix && await jobs.GetAsync(turn.JobId, cancellationToken).ConfigureAwait(false) is { } job ? job.FixRounds : (int?)null;
        var shown = Blank(model) ?? Blank(turn.Model)
            ?? (turn.Profile is { } p && models?.Value.Profiles.TryGetValue(p, out var profile) == true ? Blank(profile.Model) : null);
        var key = $"{step}|{round}|{turn.Profile}|{shown}|{turn.Effort}";
        lock (_gate)
        {
            if (_last.TryGetValue(turn.JobId.Value, out var last) && last == key)
            {
                return;   // the same step resuming
            }

            _last[turn.JobId.Value] = key;
        }

        var line = Line(step, round, turn.Profile, shown, turn.Effort);
        await outbox.TryEnqueueAsync(turn.JobId, new OutboundMessage(MessageKind.Info, line), cancellationToken).ConfigureAwait(false);
        await events.AppendAsync(turn.JobId, EventType,
            JsonSerializer.Serialize(new { step, round, profile = turn.Profile, model = shown, effort = turn.Effort, line }), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"📝 Plan · Claude · claude-opus-5-5 · effort high", "🔧 Fix round 2 · deepseek · deepseek-flash".</summary>
    public static string Line(string step, int? round, string? profile, string? model, string? effort)
    {
        var (icon, title) = step switch
        {
            JobSteps.Plan => ("📝", "Plan"),
            JobSteps.Implement => ("🔨", "Implement"),
            JobSteps.Fix => ("🔧", round is { } r ? $"Fix round {r}" : "Fix"),
            JobSteps.Handoff => ("🎓", "Hand-off"),
            _ => ("▶️", step),
        };
        return $"{icon} {title} · {profile ?? "Claude"} · {model ?? "default model"}{(Blank(effort) is { } e ? $" · effort {e}" : string.Empty)}";
    }

    /// <summary>"claude-opus-5-5 · effort high" for a review or chat that has no step line of its own.</summary>
    public static string ModelNote(string? model, string? effort) =>
        $"🧠 {Blank(model) ?? "default model"}{(Blank(effort) is { } e ? $" · effort {e}" : string.Empty)}";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
