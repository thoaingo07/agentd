using System.Globalization;
using Agentd.Application.Abstractions;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

/// <summary>The agent submits its plan with an estimate (MCP <c>submit_plan</c>).</summary>
public sealed record SubmitPlan(JobId JobId, string Plan, int EstimateMinutes, int EstimateUsagePercent);

/// <summary>What the agent should do after submitting.</summary>
public enum PlanOutcome
{
    /// <summary>Posted for approval: the job waits; the agent ends its turn.</summary>
    AwaitingApproval,

    /// <summary>Posted for information (no approval needed): the agent continues.</summary>
    Continue,
}

/// <summary>
/// Records the estimate and posts the plan. When the job needs approval it is asked as a question with
/// "✅ Approve plan" / "✏️ Request changes", and the job waits; otherwise it is posted and work continues.
/// </summary>
public sealed class SubmitPlanHandler(IJobRepository jobs, IOutbox outbox, JobActivity activity, IClock clock, IJobPlans? plans = null) : ICommandHandler<SubmitPlan, PlanOutcome>
{
    public const string ApproveLabel = "✅ Approve plan";
    public const string ChangesLabel = "✏️ Request changes";
    public const int MaxPlanLength = 1800;

    private static readonly HashSet<string> s_approvals = new(StringComparer.OrdinalIgnoreCase)
    {
        "1", "approve", "approved", "approve plan", ApproveLabel, "lgtm", "ok", "okay", "yes", "go", "go ahead", "ship it", "👍", "✅",
    };

    public async Task<Result<PlanOutcome>> Handle(SubmitPlan command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Plan) || command.Plan.Length > MaxPlanLength)
        {
            return DomainError.Validation($"The plan must be 1–{MaxPlanLength} characters (link to files for details).");
        }

        var job = await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return DomainError.NotFound($"Job {command.JobId}");
        }

        var usageNow = activity.Get(job.Id).Usage?.FiveHour;
        var submitted = job.SubmitPlan(new PlanEstimate(command.EstimateMinutes, command.EstimateUsagePercent, usageNow, clock.UtcNow));
        if (!submitted.IsSuccess)
        {
            return submitted.Error;
        }

        activity.SetPhase(job.Id, "plan");
        if (plans is not null)
        {
            // Kept for a session that starts mid-job on another model profile (its handoff carries the plan).
            await plans.SaveAsync(job.Id, command.Plan.Trim(), clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        var estimate = string.Create(CultureInfo.InvariantCulture,
            $"**Estimate:** ~{command.EstimateMinutes} min, ~{command.EstimateUsagePercent}% of the 5-hour usage window (now at {JobActivity.Percent(usageNow)}).");
        if (job.PlanStatus == PlanStatus.Pending)
        {
            var asked = job.AskDeveloper($"📝 **Plan for your approval**\n\n{command.Plan.Trim()}\n\n{estimate}", [ApproveLabel, ChangesLabel]);
            if (!asked.IsSuccess)
            {
                return asked.Error;
            }

            var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            return saved.IsSuccess ? PlanOutcome.AwaitingApproval : saved.Error;
        }

        var stored = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info, $"📝 **Plan**\n\n{command.Plan.Trim()}\n\n{estimate}"), cancellationToken).ConfigureAwait(false);
        return PlanOutcome.Continue;
    }

    /// <summary>Whether a reply to the plan question approves it ("1", the button, "approve", "lgtm", 👍, …).</summary>
    public static bool IsApproval(string reply) =>
        reply is not null && (s_approvals.Contains(reply.Trim().TrimEnd('.', '!')) || reply.TrimStart().StartsWith("approve", StringComparison.OrdinalIgnoreCase));
}
