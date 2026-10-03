using System.Globalization;
using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Jobs;

/// <summary>
/// Jobs waiting for a developer: post a reminder at 50% and at 90% of
/// <see cref="JobOptions.WaitForHumanTimeout"/>, and fail the job when it expires. Returns the
/// number of jobs changed. Run by the scheduler every minute.
/// </summary>
public sealed record CheckWaitingJobs;

public sealed class CheckWaitingJobsHandler(IJobRepository jobs, IClock clock, IOptions<JobOptions> options) : ICommandHandler<CheckWaitingJobs, int>
{
    public async Task<Result<int>> Handle(CheckWaitingJobs command, CancellationToken cancellationToken)
    {
        var timeout = options.Value.WaitForHumanTimeout;
        var changed = 0;
        foreach (var job in (await jobs.ListByStateAsync([JobState.WaitingForHuman], cancellationToken).ConfigureAwait(false)).Where(j => j.Handoff != HandoffStatus.Closing))
        {
            var since = job.WaitingSince ?? job.UpdatedAt;
            var waited = clock.UtcNow - since;
            var result = waited >= timeout
                ? job.Fail(string.Create(CultureInfo.InvariantCulture, $"No answer from the developer within {timeout.TotalHours:0.#} hours."))
                : waited >= timeout * 0.9 && job.WaitReminders < 2 ? job.RemindWaiting(2, since + timeout)
                : waited >= timeout * 0.5 && job.WaitReminders < 1 ? job.RemindWaiting(1, since + timeout)
                : Result.Fail(DomainError.Validation("nothing due"));
            // A reply that lands meanwhile wins: the version check rejects this save.
            if (result.IsSuccess && (await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false)).IsSuccess)
            {
                changed++;
            }
        }

        return changed;
    }
}
