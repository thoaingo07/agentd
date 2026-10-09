using System.Globalization;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;

namespace Agentd.Host.Workers;

/// <summary>
/// Resource use from Linux <c>/proc</c> (no external tools): a process tree's CPU from <c>/proc/&lt;pid&gt;/stat</c> utime+stime
/// deltas (USER_HZ = 100) and its resident memory (rss pages); the machine from <c>/proc/stat</c> and <c>/proc/meminfo</c>.
/// Elsewhere it returns nothing. Work inside Docker runs in the Docker daemon, so it shows in the machine, not the job.
/// </summary>
internal sealed class ProcResourceSampler(string procRoot = "/proc") : IResourceSampler
{
    private const double TicksPerSecond = 100;   // USER_HZ, 100 on every mainstream Linux

    private readonly Dictionary<int, long> _lastTicks = [];
    private readonly Lock _gate = new();
    private DateTimeOffset _lastSample = DateTimeOffset.MinValue;
    private (long Busy, long Total)? _lastCpu;

    public IReadOnlyDictionary<long, (double CpuPercent, long MemoryBytes)> SampleTrees(IReadOnlyDictionary<long, int> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (!Directory.Exists(procRoot) || roots.Count == 0)
        {
            return new Dictionary<long, (double, long)>();
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = _lastSample == DateTimeOffset.MinValue ? 0 : (now - _lastSample).TotalSeconds;
            _lastSample = now;
            var stats = ReadAllStats();
            var children = stats.Values.GroupBy(p => p.Parent).ToDictionary(g => g.Key, g => g.Select(p => p.Pid).ToList());
            var result = new Dictionary<long, (double, long)>();
            foreach (var (job, root) in roots)
            {
                double cpu = 0;
                long memory = 0;
                foreach (var pid in Tree(root, children))
                {
                    if (!stats.TryGetValue(pid, out var p))
                    {
                        continue;
                    }

                    memory += p.RssPages * Environment.SystemPageSize;
                    // A process seen for the first time contributes from the next sample on (no spike at its start).
                    if (elapsed > 0 && _lastTicks.TryGetValue(pid, out var before) && p.Ticks >= before)
                    {
                        cpu += (p.Ticks - before) / TicksPerSecond / elapsed * 100;
                    }
                }

                result[job] = (Math.Round(cpu), memory);
            }

            _lastTicks.Clear();
            foreach (var p in stats.Values)
            {
                _lastTicks[p.Pid] = p.Ticks;
            }

            return result;
        }
    }

    public MachineResources? SampleMachine(string diskPath)
    {
        try
        {
            var cpuLine = File.ReadLines(Path.Combine(procRoot, "stat")).First(l => l.StartsWith("cpu ", StringComparison.Ordinal));
            var values = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(v => long.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var idle = values[3] + (values.Length > 4 ? values[4] : 0);   // idle + iowait
            var total = values.Sum();
            double cpu = 0;
            lock (_gate)
            {
                if (_lastCpu is { } last && total > last.Total)
                {
                    cpu = Math.Round(100.0 * ((total - idle) - last.Busy) / (total - last.Total));
                }

                _lastCpu = (total - idle, total);
            }

            var memory = File.ReadLines(Path.Combine(procRoot, "meminfo"))
                .Select(l => l.Split(':', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => long.Parse(p[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture) * 1024);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(diskPath))!);
            var cores = File.ReadLines(Path.Combine(procRoot, "stat")).Count(l => l.Length > 3 && l.StartsWith("cpu", StringComparison.Ordinal) && char.IsAsciiDigit(l[3]));
            var load = TryRead(Path.Combine(procRoot, "loadavg"))?.Split(' ') is { Length: >= 3 } l ? l : null;
            var uptime = TryRead(Path.Combine(procRoot, "uptime"))?.Split(' ')[0] is { } u && double.TryParse(u, NumberStyles.Float, CultureInfo.InvariantCulture, out var up) ? TimeSpan.FromSeconds(Math.Floor(up)) : (TimeSpan?)null;
            return new MachineResources(cpu, memory.GetValueOrDefault("MemTotal"), memory.GetValueOrDefault("MemAvailable"),
                drive.TotalSize, drive.AvailableFreeSpace, DateTimeOffset.UtcNow)
            {
                Details = new MachineDetails(cores, Number(load, 0), Number(load, 1), Number(load, 2),
                    memory.GetValueOrDefault("SwapTotal"), memory.GetValueOrDefault("SwapFree"), uptime, Disks(Path.GetFullPath(diskPath))),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;   // not Linux, or /proc looks different: no machine numbers
        }
    }

    /// <summary>Filesystems that are real disks; tmpfs, overlay, squashfs (snaps), proc and the like are left out.</summary>
    private static readonly HashSet<string> s_diskTypes = ["ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "bcachefs"];

    private static double? Number(string[]? fields, int i) =>
        fields is not null && double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Each real disk once (btrfs subvolumes mount one device many times), from <c>/proc/mounts</c>.</summary>
    internal List<DiskUse> Disks(string home)
    {
        var mounts = (TryRead(Path.Combine(procRoot, "mounts")) ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' '))
            .Where(f => f.Length >= 3 && s_diskTypes.Contains(f[2]))
            .Select(f => (Device: f[0], Mount: f[1].Replace("\\040", " ", StringComparison.Ordinal)))
            .DistinctBy(m => m.Device)
            .ToList();
        var homeMount = mounts.Select(m => m.Mount).Where(m => IsUnder(home, m)).MaxBy(m => m.Length);
        var disks = new List<DiskUse>();
        foreach (var (_, mount) in mounts)
        {
            try
            {
                var drive = new DriveInfo(mount);
                if (drive.TotalSize > 0)
                {
                    disks.Add(new DiskUse(mount, drive.TotalSize, drive.AvailableFreeSpace, mount == homeMount));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // unmounted meanwhile, or not ours to read
            }
        }

        return disks;
    }

    private static bool IsUnder(string path, string mount) =>
        mount == "/" || path == mount || path.StartsWith(mount + "/", StringComparison.Ordinal);

    /// <summary>The root and every descendant.</summary>
    private static IEnumerable<int> Tree(int root, Dictionary<int, List<int>> children)
    {
        var queue = new Queue<int>([root]);
        var seen = new HashSet<int>();
        while (queue.TryDequeue(out var pid))
        {
            if (!seen.Add(pid))
            {
                continue;
            }

            yield return pid;
            foreach (var child in children.GetValueOrDefault(pid) ?? [])
            {
                queue.Enqueue(child);
            }
        }
    }

    private Dictionary<int, ProcStat> ReadAllStats()
    {
        var stats = new Dictionary<int, ProcStat>();
        foreach (var dir in Directory.EnumerateDirectories(procRoot))
        {
            if (int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                && TryRead(Path.Combine(dir, "stat")) is { } line && Parse(pid, line) is { } stat)
            {
                stats[pid] = stat;
            }
        }

        return stats;
    }

    /// <summary>
    /// <c>pid (comm) state ppid … utime stime … rss …</c>: the command name may hold spaces and parentheses, so the fields
    /// are counted after the last ')'. After it: [0] state, [1] ppid, [11] utime, [12] stime, [21] rss.
    /// </summary>
    internal static ProcStat? Parse(int pid, string line)
    {
        var close = line.LastIndexOf(')');
        if (close < 0)
        {
            return null;
        }

        var f = line[(close + 2)..].Split(' ');
        return f.Length > 21
            && int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
            && long.TryParse(f[11], NumberStyles.None, CultureInfo.InvariantCulture, out var user)
            && long.TryParse(f[12], NumberStyles.None, CultureInfo.InvariantCulture, out var system)
            && long.TryParse(f[21], NumberStyles.None, CultureInfo.InvariantCulture, out var rss)
            ? new ProcStat(pid, parent, user + system, rss)
            : null;
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;   // the process ended meanwhile
        }
    }

    internal sealed record ProcStat(int Pid, int Parent, long Ticks, long RssPages);
}
