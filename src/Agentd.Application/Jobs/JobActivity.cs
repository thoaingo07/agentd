using System.Collections.Concurrent;
using System.Globalization;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

/// <summary>Subscription usage of the 5-hour and weekly windows (0–1), as Claude Code reported it last.</summary>
public sealed record UsageSnapshot(double? FiveHour, double? Weekly, DateTimeOffset? ResetsAt);

/// <summary>What a running job is doing right now (in memory; rebuilt as the agent works).</summary>
/// <param name="Phase">The agent's phase (<c>set_phase</c>).</param>
/// <param name="LastActivity">Its latest step, e.g. "🔧 dotnet test".</param>
/// <param name="LastActivityAt">When that step started.</param>
/// <param name="Usage">The subscription usage, as last reported.</param>
/// <param name="Running">A tool call is still in flight (started, no result yet): <paramref name="LastActivity"/> is still running.</param>
/// <param name="Output">Where the running command's output is being written, when the CLI says (live tail).</param>
/// <param name="Resources">The agent's CPU, memory and worktree size, as last sampled.</param>
public sealed record ActivitySnapshot(
    string? Phase, string? LastActivity, DateTimeOffset? LastActivityAt, UsageSnapshot? Usage, bool Running = false, CommandOutput? Output = null,
    JobResources? Resources = null);

/// <summary>One job's resource use: its agent's whole process tree (claude and everything it started) and its worktree.</summary>
public sealed record JobResources(double CpuPercent, long MemoryBytes, long? WorktreeBytes, DateTimeOffset At)
{
    /// <summary>"CPU 180% · RAM 2.1 GB · disk 450 MB" (CPU is percent of one core).</summary>
    public string Describe() =>
        $"CPU {CpuPercent.ToString("0", CultureInfo.InvariantCulture)}% · RAM {JobActivity.Bytes(MemoryBytes)}" + (WorktreeBytes is { } d ? $" · disk {JobActivity.Bytes(d)}" : string.Empty);
}

/// <summary>The machine agentd runs on: CPU busy share, memory, and the disk holding agentd's home.</summary>
public sealed record MachineResources(double CpuPercent, long MemoryTotal, long MemoryAvailable, long DiskTotal, long DiskFree, DateTimeOffset At)
{
    /// <summary>Under 10% of memory available.</summary>
    public bool LowMemory => MemoryTotal > 0 && MemoryAvailable < MemoryTotal / 10;

    /// <summary>Under 5 GB or 5% free.</summary>
    public bool LowDisk => DiskTotal > 0 && (DiskFree < 5L * 1024 * 1024 * 1024 || DiskFree < DiskTotal / 20);

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"CPU {CpuPercent:0}% · RAM {JobActivity.Bytes(MemoryAvailable)} free of {JobActivity.Bytes(MemoryTotal)} · disk {JobActivity.Bytes(DiskFree)} free of {JobActivity.Bytes(DiskTotal)}");
}

/// <summary>A running command's output file, as Claude Code writes it: <c>&lt;tmp&gt;/claude-&lt;uid&gt;/&lt;project&gt;/&lt;session&gt;/tasks/&lt;task&gt;.output</c>.</summary>
public sealed record CommandOutput(string Session, string TaskId)
{
    public const int TailBytes = 4096;

    /// <summary>The last <paramref name="lines"/> lines written so far, or null when the file isn't there (undocumented CLI behavior: best effort).</summary>
    public string? Tail(int lines = 15, string? tempRoot = null)
    {
        try
        {
            var root = tempRoot ?? Path.GetTempPath();
            var file = Directory.EnumerateDirectories(root, "claude-*")
                .SelectMany(Directory.EnumerateDirectories)
                .Select(project => Path.Combine(project, Session, "tasks", TaskId + ".output"))
                .FirstOrDefault(File.Exists);
            if (file is null)
            {
                return null;
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - TailBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd('\n');
            var last = text.Split('\n').TakeLast(lines);
            return string.Join('\n', last);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Live job activity for status replies and the heartbeat: the agent's phase (<c>set_phase</c>),
/// its latest tool step and the subscription usage, fed by the Claude runner. In memory only: after a
/// restart it fills again as the agent works.
/// </summary>
public sealed class JobActivity : Ports.IAgentActivitySink
{
    public static readonly string[] Phases = ["clarify", "plan", "implement", "verify", "fix", "handoff"];
    private static readonly double[] s_warnAt = [0.8, 0.95];

    private readonly ConcurrentDictionary<long, ActivitySnapshot> _jobs = new();
    private readonly ConcurrentDictionary<(long Job, string Window), double> _warned = new();
    private readonly ConcurrentDictionary<long, bool> _stuck = new();
    private readonly ConcurrentDictionary<(long Job, string What), bool> _resourceWarnings = new();

    /// <summary>The machine's latest sample (null until the first one, or off Linux).</summary>
    public MachineResources? Machine { get; private set; }

    public void RecordMachine(MachineResources machine) => Machine = machine;

    public void RecordResources(JobId job, JobResources resources) =>
        _jobs.AddOrUpdate(job.Value, _ => new(null, null, null, null, Resources: resources), (_, s) => s with { Resources = resources });

    /// <summary>Records whether a resource warning (e.g. "disk") applies to the job; true when that changed, so it's posted once.</summary>
    public bool ResourceWarningChanged(JobId job, string what, bool on)
    {
        var before = _resourceWarnings.GetValueOrDefault((job.Value, what));
        _resourceWarnings[(job.Value, what)] = on;
        return before != on;
    }

    /// <summary>1536 → "2 KB", 2.1e9 → "2 GB" (binary units, short).</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.#} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, bytes) / 1024} KB"),
    };

    void Ports.IAgentActivitySink.ToolStep(JobId jobId, string description, DateTimeOffset at) => RecordActivity(jobId, description, at, running: true);

    void Ports.IAgentActivitySink.ToolFinished(JobId jobId) =>
        _jobs.AddOrUpdate(jobId.Value, _ => new(null, null, null, null), (_, s) => s with { Running = false, Output = null });

    void Ports.IAgentActivitySink.CommandStarted(JobId jobId, string session, string taskId) =>
        _jobs.AddOrUpdate(jobId.Value, _ => new(null, null, null, null, true, new(session, taskId)), (_, s) => s with { Output = new(session, taskId) });

    void Ports.IAgentActivitySink.Usage(JobId jobId, double? fiveHour, double? weekly, DateTimeOffset? resetsAt) =>
        RecordUsage(jobId, new UsageSnapshot(fiveHour, weekly, resetsAt));

    public ActivitySnapshot Get(JobId job) => _jobs.TryGetValue(job.Value, out var s) ? s : new ActivitySnapshot(null, null, null, null);

    public void SetPhase(JobId job, string phase) => _jobs.AddOrUpdate(job.Value, _ => new(phase, null, null, null), (_, s) => s with { Phase = phase });

    /// <param name="job">The job.</param>
    /// <param name="description">What it does, e.g. "🔧 dotnet test".</param>
    /// <param name="at">When it started.</param>
    /// <param name="running">It's a step still in progress (a tool call, a permitted command) rather than a finished event.</param>
    public void RecordActivity(JobId job, string description, DateTimeOffset at, bool running = false) =>
        _jobs.AddOrUpdate(job.Value, _ => new(null, description, at, null, running),
            (_, s) => s with { LastActivity = description, LastActivityAt = at, Running = running, Output = running ? s.Output : null });

    public void RecordUsage(JobId job, UsageSnapshot usage) =>
        _jobs.AddOrUpdate(job.Value, _ => new(null, null, null, usage), (_, s) => s with { Usage = usage });

    /// <summary>Usage thresholds (80%, 95%) crossed since the last call, once per job and window.</summary>
    public IReadOnlyList<(string Window, double Utilization)> TakeUsageWarnings(JobId job)
    {
        var usage = Get(job).Usage;
        var due = new List<(string, double)>();
        foreach (var (window, value) in new[] { ("5-hour", usage?.FiveHour), ("weekly", usage?.Weekly) })
        {
            var level = s_warnAt.Where(t => value >= t).DefaultIfEmpty(0).Max();
            if (level > 0 && level > _warned.GetValueOrDefault((job.Value, window)))
            {
                _warned[(job.Value, window)] = level;
                due.Add((window, value!.Value));
            }
        }

        return due;
    }

    /// <summary>Records whether the job looks stuck; true when that changed (so the change is announced once).</summary>
    public bool StuckChanged(JobId job, bool stuck)
    {
        var before = _stuck.GetValueOrDefault(job.Value);
        _stuck[job.Value] = stuck;
        return before != stuck;
    }

    public void Forget(JobId job)
    {
        _jobs.TryRemove(job.Value, out _);
        _stuck.TryRemove(job.Value, out _);
        foreach (var key in _resourceWarnings.Keys.Where(k => k.Job == job.Value))
        {
            _resourceWarnings.TryRemove(key, out _);
        }

        foreach (var key in _warned.Keys.Where(k => k.Job == job.Value))
        {
            _warned.TryRemove(key, out _);
        }
    }

    /// <summary>One line for status replies and the heartbeat, e.g. "🟢 working · implement · 📖 reading X (20 s ago) · 12 min elapsed".</summary>
    public static string Describe(Job job, ActivitySnapshot activity, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(activity);
        var parts = new List<string>();
        if (job.State == JobState.WaitingForHuman)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"⏸ waiting for you since {job.WaitingSince ?? job.UpdatedAt:HH:mm} UTC"));
        }
        else
        {
            parts.Add($"🟢 {job.State.ToString().ToLowerInvariant()}");
            if (activity.LastActivity is { } last && activity.LastActivityAt is { } at)
            {
                parts.Add(activity.Running ? $"{last} (running for {Ago(now - at)})" : $"{last} ({Ago(now - at)} ago)");
            }
        }

        if (activity.Phase is { } phase)
        {
            parts.Insert(1, phase);
        }

        if (job.State is JobState.Running && activity.Resources is { } resources && now - resources.At < TimeSpan.FromMinutes(1))
        {
            parts.Add(resources.Describe());
        }

        parts.Add($"{Ago(now - job.CreatedAt)} elapsed");
        if (activity.Usage is { } usage && (usage.FiveHour is not null || usage.Weekly is not null))
        {
            parts.Add($"usage 5h {Percent(usage.FiveHour)} / week {Percent(usage.Weekly)}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The status line, plus the running command's latest output when there is some (status replies, the stuck warning).</summary>
    public static string DescribeWithOutput(Job job, ActivitySnapshot activity, DateTimeOffset now, string? tempRoot = null)
    {
        var line = Describe(job, activity, now);
        return activity is { Running: true, Output: { } output } && output.Tail(tempRoot: tempRoot) is { Length: > 0 } tail
            ? $"{line}\n```\n{tail.Replace("```", "ʼʼʼ", StringComparison.Ordinal)}\n```"
            : line;
    }

    /// <summary>0.62 → "62%"; unknown → "?".</summary>
    public static string Percent(double? fraction) =>
        fraction is { } f ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(f * 100):0}%") : "?";

    public static string Ago(TimeSpan t) => t switch
    {
        { TotalMinutes: < 1 } => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)t.TotalSeconds)} s"),
        { TotalHours: < 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes} min"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours} h {t.Minutes} min"),
    };
}
