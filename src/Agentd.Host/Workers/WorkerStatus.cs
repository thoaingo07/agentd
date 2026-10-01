using Agentd.Application.Jobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Workers;

/// <summary>Live state of the daemon loop, for health details (and later the dashboard).</summary>
internal sealed class WorkerStatus
{
    public DateTimeOffset? LastPollAt { get; set; }

    public DateTimeOffset? NextPollAt { get; set; }

    public int ConsecutivePollFailures { get; set; }

    public string? LastPollError { get; set; }
}

/// <summary>Reports polling and scheduling state; degraded while polling keeps failing.</summary>
internal sealed class WorkerHealthCheck(WorkerStatus status, JobDispatcher dispatcher, IOptions<SchedulerOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        var data = new Dictionary<string, object>
        {
            ["enabled"] = o.Enabled,
            ["pollInterval"] = o.PollInterval.ToString(),
            ["nextPollAt"] = status.NextPollAt?.ToString("O") ?? "",
            ["lastPollAt"] = status.LastPollAt?.ToString("O") ?? "",
            ["runningAgents"] = dispatcher.ActiveCount,
            ["maxConcurrent"] = o.MaxConcurrent,
        };
        var result = status.ConsecutivePollFailures > 0
            ? HealthCheckResult.Degraded($"Polling failed {status.ConsecutivePollFailures} time(s): {status.LastPollError}", data: data)
            : HealthCheckResult.Healthy(data: data);
        return Task.FromResult(result);
    }
}
