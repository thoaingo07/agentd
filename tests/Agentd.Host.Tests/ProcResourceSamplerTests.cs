using Agentd.Host.Workers;

namespace Agentd.Host.Tests;

[TestClass]
public sealed class ProcResourceSamplerTests : IDisposable
{
    private readonly string _proc = Directory.CreateTempSubdirectory("agentd-proc-").FullName;

    public void Dispose() => Directory.Delete(_proc, recursive: true);

    [TestMethod]
    public void A_stat_line_is_parsed_even_when_the_command_name_has_spaces_and_parentheses()
    {
        var stat = ProcResourceSampler.Parse(123, Stat(123, "my (weird) proc", parent: 7, utime: 40, stime: 2, rss: 300));

        Assert.AreEqual(new ProcResourceSampler.ProcStat(123, 7, 42, 300), stat);
        Assert.IsNull(ProcResourceSampler.Parse(1, "garbage"));
    }

    [TestMethod]
    public void A_jobs_tree_adds_up_its_processes_and_leaves_others_out()
    {
        Write(100, "claude", parent: 1, utime: 100, rss: 1000);
        Write(101, "dotnet test", parent: 100, utime: 50, rss: 2000);
        Write(102, "sh", parent: 101, utime: 0, rss: 10);
        Write(200, "postgres", parent: 1, utime: 900, rss: 99999);
        WriteMachine(busy: 1000, idle: 9000);
        var sampler = new ProcResourceSampler(_proc);
        sampler.SampleTrees(new Dictionary<long, int> { [7] = 100 });
        Assert.AreEqual(0, sampler.SampleMachine(_proc)!.CpuPercent, "the first machine sample is the baseline");

        Write(100, "claude", parent: 1, utime: 110, rss: 1000);
        Write(101, "dotnet test", parent: 100, utime: 250, rss: 2000);
        WriteMachine(busy: 1600, idle: 9400);
        var tree = sampler.SampleTrees(new Dictionary<long, int> { [7] = 100 })[7];
        var machine = sampler.SampleMachine(_proc)!;

        Assert.AreEqual((1000 + 2000 + 10) * (long)Environment.SystemPageSize, tree.MemoryBytes, "the whole tree, not postgres");
        Assert.IsGreaterThan(0, tree.CpuPercent, "210 ticks since the last sample");
        Assert.AreEqual(60, machine.CpuPercent, "600 busy of 1000 ticks");
        Assert.AreEqual((16L << 30, 4L << 30), (machine.MemoryTotal, machine.MemoryAvailable));
        Assert.IsGreaterThan(0, machine.DiskTotal);
    }

    [TestMethod]
    public void The_machine_has_cores_load_swap_uptime_and_each_real_disk_once()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux only");
        }

        WriteMachine(busy: 1000, idle: 9000);
        File.AppendAllText(Path.Combine(_proc, "stat"), "cpu1 1 0 0 1 0 0 0 0 0 0\nintr 5\n");
        File.AppendAllText(Path.Combine(_proc, "meminfo"), "SwapTotal:       8388608 kB\nSwapFree:        8000000 kB\n");
        File.WriteAllText(Path.Combine(_proc, "loadavg"), "0.77 1.43 0.90 2/1234 5678\n");
        File.WriteAllText(Path.Combine(_proc, "uptime"), "130812.45 2000000.00\n");
        File.WriteAllText(Path.Combine(_proc, "mounts"), """
            /dev/nvme0n1p4 / ext4 rw,relatime 0 0
            tmpfs /run tmpfs rw 0 0
            /dev/nvme0n1p4 /var/snap ext4 rw 0 0
            overlay /var/lib/docker/overlay2/x/merged overlay rw 0 0
            """);

        var details = new ProcResourceSampler(_proc).SampleMachine(_proc)!.Details!;

        Assert.AreEqual(2, details.Cores, "cpu0 and cpu1, not the total line");
        Assert.AreEqual((0.77, 1.43, 0.90), (details.Load1, details.Load5, details.Load15));
        Assert.AreEqual((8L << 30, 8000000L * 1024), (details.SwapTotal, details.SwapFree));
        Assert.AreEqual(TimeSpan.FromSeconds(130812), details.Uptime);
        var disk = details.Disks.Single();
        Assert.AreEqual(("/", true), (disk.Mount, disk.Home), "tmpfs and overlay left out, the second mount of the same device too");
        Assert.IsGreaterThan(0, disk.Total);
    }

    [TestMethod]
    public void Without_loadavg_uptime_or_mounts_the_details_are_unknown_not_an_error()
    {
        WriteMachine(busy: 1000, idle: 9000);

        var details = new ProcResourceSampler(_proc).SampleMachine(_proc)!.Details!;

        Assert.AreEqual((1, (double?)null, (TimeSpan?)null), (details.Cores, details.Load1, details.Uptime));
        Assert.IsEmpty(details.Disks);
    }

    [TestMethod]
    public void On_linux_the_real_proc_reports_this_process()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux only");
        }

        var sampler = new ProcResourceSampler();
        var mine = sampler.SampleTrees(new Dictionary<long, int> { [1] = Environment.ProcessId })[1];

        Assert.IsGreaterThan(10L << 20, mine.MemoryBytes, "a .NET test host uses more than 10 MB");
        Assert.IsNotNull(sampler.SampleMachine(Path.GetTempPath()));
    }

    private static string Stat(int pid, string comm, int parent, long utime, long stime = 0, long rss = 0)
    {
        // pid (comm) state ppid pgrp session tty tpgid flags minflt cminflt majflt cmajflt utime stime cutime cstime priority nice threads itreal starttime vsize rss
        var f = new[] { "S", parent.ToString(), "0", "0", "0", "0", "0", "0", "0", "0", "0", utime.ToString(), stime.ToString(), "0", "0", "20", "0", "1", "0", "0", "0", rss.ToString(), "0" };
        return $"{pid} ({comm}) {string.Join(' ', f)}\n";
    }

    private void Write(int pid, string comm, int parent, long utime, long rss)
    {
        Directory.CreateDirectory(Path.Combine(_proc, pid.ToString()));
        File.WriteAllText(Path.Combine(_proc, pid.ToString(), "stat"), Stat(pid, comm, parent, utime, rss: rss));
    }

    private void WriteMachine(long busy, long idle)
    {
        File.WriteAllText(Path.Combine(_proc, "stat"), $"cpu  {busy} 0 0 {idle} 0 0 0 0 0 0\ncpu0 1 0 0 1 0 0 0 0 0 0\n");
        File.WriteAllText(Path.Combine(_proc, "meminfo"), "MemTotal:       16777216 kB\nMemFree:         1000000 kB\nMemAvailable:    4194304 kB\n");
    }
}
