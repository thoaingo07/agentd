using Agentd.Application.Ports;
using Agentd.Domain.Jobs;

namespace Agentd.Application.Jobs;

/// <summary>The model (and effort) for one step of the cycle (<c>Agentd:Jobs:Steps:&lt;step&gt;</c>). Empty: the runner's default.</summary>
public sealed class StepModel
{
    public string? Model { get; set; }

    public string? Effort { get; set; }

    /// <summary>A profile in <c>Agentd:Models:Profiles</c> (e.g. <c>deepseek</c>); empty: the default (the Claude subscription).</summary>
    public string? Profile { get; set; }
}

/// <summary>
/// Which step of the cycle an agent turn is, and so which model runs it. A job is one session, and each turn can use a
/// different model (<c>--resume … --model</c>): <c>plan</c> (clarify and plan, read-only, before approval),
/// <c>implement</c> (after approval, until the PR), <c>fix</c> (review fix rounds on the PR) and <c>handoff</c>.
/// </summary>
public static class JobSteps
{
    public const string Plan = "plan";
    public const string Implement = "implement";
    public const string Fix = "fix";
    public const string Handoff = "handoff";

    /// <summary>Not a job turn: the defaults for <c>!review</c> (Model, Effort; a review's own --model / --effort win).</summary>
    public const string Review = "review";

    public static readonly IReadOnlyList<string> All = [Plan, Implement, Fix, Handoff];

    public static string Of(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.Handoff is HandoffStatus.Requested or HandoffStatus.Proposing ? Handoff
            : job.PlanStatus == PlanStatus.Pending ? Plan
            : job.PullRequest is not null ? Fix
            : Implement;
    }

    /// <summary>The turn with its step's model and effort from <paramref name="options"/>, unless the turn has its own.</summary>
    public static AgentRunRequest Apply(AgentRunRequest request, JobOptions? options)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Step is null || options?.Steps.TryGetValue(request.Step, out var step) != true || step is null)
        {
            return request;
        }

        return request with
        {
            Model = request.Model ?? (string.IsNullOrWhiteSpace(step.Model) ? null : step.Model.Trim()),
            Effort = request.Effort ?? (string.IsNullOrWhiteSpace(step.Effort) ? null : step.Effort.Trim()),
            Profile = request.Profile ?? (string.IsNullOrWhiteSpace(step.Profile) ? null : step.Profile.Trim()),
        };
    }
}
