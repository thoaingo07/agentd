using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Domain.Common;

namespace Agentd.Application.Queries;

/// <summary>The machine and each running job's latest resource sample (CPU, RAM, worktree size).</summary>
public sealed record GetResources;

public sealed record ResourcesView(MachineResources? Machine, IReadOnlyDictionary<long, JobResources> Jobs);

public sealed class GetResourcesHandler(JobActivity activity, IClock clock) : IQueryHandler<GetResources, ResourcesView>
{
    /// <summary>Samples older than this are stale (the sampler runs every 5 s).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    public Task<ResourcesView> Handle(GetResources query, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var machine = activity.Machine is { } m && now - m.At < MaxAge ? m : null;
        return Task.FromResult(new ResourcesView(machine, activity.Resources(now, MaxAge)));
    }
}
