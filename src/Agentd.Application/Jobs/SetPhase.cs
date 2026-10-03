using System.Text.Json;
using Agentd.Application.Abstractions;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

/// <summary>The agent enters a lifecycle phase (MCP <c>set_phase</c>): posted to the thread and shown in status.</summary>
public sealed record SetPhase(JobId JobId, string Phase, string Summary);

public sealed class SetPhaseHandler(JobActivity activity, IOutbox outbox, IEventStore events) : ICommandHandler<SetPhase, Unit>
{
    public const int MaxSummaryLength = 3000;

    private static readonly Dictionary<string, string> s_titles = new(StringComparer.Ordinal)
    {
        ["clarify"] = "🧠 **Clarify spec**",
        ["plan"] = "📝 **Plan**",
        ["implement"] = "🛠 **Implement**",
        ["verify"] = "🧪 **Verify**",
        ["fix"] = "🔁 **Fix review feedback**",
        ["handoff"] = "🎓 **Hand-off**",
    };

    public async Task<Result<Unit>> Handle(SetPhase command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var phase = command.Phase.Trim().ToLowerInvariant();
        if (!s_titles.TryGetValue(phase, out var title))
        {
            return DomainError.Validation($"Unknown phase '{command.Phase}'. Use one of: {string.Join(", ", JobActivity.Phases)}.");
        }

        if (string.IsNullOrWhiteSpace(command.Summary) || command.Summary.Length > MaxSummaryLength)
        {
            return DomainError.Validation($"The summary must be 1–{MaxSummaryLength} characters.");
        }

        activity.SetPhase(command.JobId, phase);
        await events.AppendAsync(command.JobId, "phase.set", JsonSerializer.Serialize(new { phase, summary = command.Summary }), cancellationToken).ConfigureAwait(false);
        await outbox.EnqueueAsync(command.JobId, [new OutboxMessage(new OutboundMessage(MessageKind.Info, $"{title}\n\n{command.Summary.Trim()}"))], cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
