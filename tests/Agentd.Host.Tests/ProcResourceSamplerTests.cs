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
