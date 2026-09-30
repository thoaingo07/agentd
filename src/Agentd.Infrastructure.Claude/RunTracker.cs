using Agentd.Application.Ports;

namespace Agentd.Infrastructure.Claude;

/// <summary>
/// Follows a run's events to produce the outcome summary: the final result, and whether the subscription
/// hit a usage limit (a limited <see cref="AgentEvent.RateLimit"/>, or a 429 in the result).
/// </summary>
public sealed class RunTracker
{
    public string? SessionId { get; private set; }

    public AgentEvent.TurnResult? Result { get; private set; }

    public AgentEvent.RateLimit? LastRateLimit { get; private set; }

    public void Observe(AgentEvent agentEvent)
    {
        switch (agentEvent)
        {
            case AgentEvent.SessionStarted s:
                SessionId = s.SessionId;
                break;
            case AgentEvent.RateLimit r:
                LastRateLimit = r;
                break;
            case AgentEvent.TurnResult t:
                Result = t;
                break;
        }
    }

    /// <summary>True when the run ended because of a usage limit.</summary>
    public bool UsageLimited =>
        LastRateLimit is { IsLimited: true }
        || Result is { ApiErrorStatus: 429 }
        || (Result is { IsError: true, Result: { } text } && text.Contains("usage limit", StringComparison.OrdinalIgnoreCase));

    public DateTimeOffset? LimitResetsAt => LastRateLimit?.ResetsAt;

    public AgentResultSummary? Summary => Result is null ? null : new AgentResultSummary(Result.Turns, Result.IsError ? Result.Subtype : null, Result.IsError);
}
