using Agentd.Application.Queries;

namespace Agentd.Bff.ViewModels;

/// <summary>The machine agentd runs on (null fields: not sampled, e.g. off Linux).</summary>
public sealed record MachineVm(double CpuPercent, long MemoryTotal, long MemoryAvailable, long DiskTotal, long DiskFree, bool LowMemory, bool LowDisk, DateTimeOffset At, MachineDetailsVm? Details);

/// <summary>Cores, load averages (null: unknown), swap, uptime in seconds, and each real disk.</summary>
public sealed record MachineDetailsVm(int Cores, double? Load1, double? Load5, double? Load15, long SwapTotal, long SwapFree, long? UptimeSeconds, IReadOnlyList<DiskVm> Disks);

/// <summary>A mounted disk; <c>home</c>: it holds agentd's home (worktrees, logs).</summary>
public sealed record DiskVm(string Mount, long Total, long Free, bool Home);

/// <summary>A running job's agent process tree (CPU is percent of one core) and its worktree size.</summary>
public sealed record JobResourcesVm(long JobId, double CpuPercent, long MemoryBytes, long? WorktreeBytes, DateTimeOffset At);

public sealed record ResourcesVm(MachineVm? Machine, IReadOnlyList<JobResourcesVm> Jobs)
{
    public static ResourcesVm From(ResourcesView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return new(
            v.Machine is { } m ? new MachineVm(m.CpuPercent, m.MemoryTotal, m.MemoryAvailable, m.DiskTotal, m.DiskFree, m.LowMemory, m.LowDisk, m.At,
                m.Details is { } d ? new MachineDetailsVm(d.Cores, d.Load1, d.Load5, d.Load15, d.SwapTotal, d.SwapFree, (long?)d.Uptime?.TotalSeconds,
                    [.. d.Disks.Select(x => new DiskVm(x.Mount, x.Total, x.Free, x.Home))]) : null) : null,
            [.. v.Jobs.OrderBy(kv => kv.Key).Select(kv => new JobResourcesVm(kv.Key, kv.Value.CpuPercent, kv.Value.MemoryBytes, kv.Value.WorktreeBytes, kv.Value.At))]);
    }
}
