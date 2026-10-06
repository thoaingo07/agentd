using Agentd.Host.Cli;

namespace Agentd.Host.Tests.Cli;

[TestClass]
public sealed class DaemonServiceTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("agentd-xdg-").FullName;
    private readonly List<string> _calls = [];
    private readonly Dictionary<string, ProcessResult> _results = [];
    private string? _home;

    public void Dispose() => Directory.Delete(_config, recursive: true);

    [TestMethod]
    public void The_unit_runs_the_daemon_quotes_its_arguments_and_holds_no_secrets()
    {
        var unit = SystemdUnit.Render(SystemdUnit.ExecStart("/opt/my apps/agentd", null), "/srv/agentd 100%");

        StringAssert.Contains(unit, "ExecStart=\"/opt/my apps/agentd\" \"daemon\" \"run\"");
        StringAssert.Contains(unit, "Environment=\"AGENTD_HOME=/srv/agentd 100%%\"", "% starts a systemd specifier");
        StringAssert.Contains(unit, "Restart=on-failure");
        StringAssert.Contains(unit, "WantedBy=default.target");
        Assert.DoesNotContain("PAT", unit.Replace("Description", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain("AGENTD_HOME", SystemdUnit.Render(SystemdUnit.ExecStart("/usr/bin/agentd", null), null), "the default home needs no variable");
        CollectionAssert.AreEqual(new[] { "/usr/bin/dotnet", "/src/agentd.dll", "daemon", "run" }, SystemdUnit.ExecStart("/usr/bin/dotnet", "/src/agentd.dll").ToArray(), "a development build");
    }

    [TestMethod]
    public async Task Install_writes_the_user_unit_enables_it_and_hints_at_lingering()
    {
        _home = "/srv/agentd";
        _results["loginctl show-user tngo --property=Linger"] = new(0, "Linger=no");

        var (code, output, _) = await Cli("daemon", "install");

        Assert.AreEqual(ExitCodes.Ok, code);
        var unit = await File.ReadAllTextAsync(Path.Combine(_config, "systemd", "user", "agentd.service"));
        StringAssert.Contains(unit, "Environment=\"AGENTD_HOME=/srv/agentd\"");
        CollectionAssert.IsSubsetOf(new[] { "systemctl --user daemon-reload", "systemctl --user enable agentd.service" }, _calls);
        StringAssert.Contains(output, "agentd daemon start");
        StringAssert.Contains(output, "sudo loginctl enable-linger tngo");
    }

    [TestMethod]
    public async Task The_service_verbs_map_to_systemctl_and_journalctl()
    {
        _results["systemctl --user status agentd.service"] = new(3, "inactive (dead)");

        Assert.AreEqual(ExitCodes.Ok, (await Cli("daemon", "start")).Code);
        Assert.AreEqual(ExitCodes.Ok, (await Cli("daemon", "status")).Code, "a stopped service is an answer, not an error");
        Assert.AreEqual(ExitCodes.Ok, (await Cli("daemon", "logs", "-f", "-n", "50")).Code);
        await Cli("daemon", "uninstall");

        CollectionAssert.IsSubsetOf(new[]
        {
            "systemctl --user start agentd.service",
            "systemctl --user status agentd.service",
            "journalctl --user -u agentd.service -n 50 --no-pager -f",
            "systemctl --user disable --now agentd.service",
        }, _calls);
    }

    [TestMethod]
    public async Task Without_systemd_it_says_how_to_run_instead()
    {
        _results["systemctl --user --version"] = new(127, "not found");

        var (code, _, error) = await Cli("daemon", "install");

        Assert.AreEqual(ExitCodes.Error, code);
        StringAssert.Contains(error, "agentd daemon run");
        Assert.IsFalse(File.Exists(Path.Combine(_config, "systemd", "user", "agentd.service")));
    }

    private async Task<(int Code, string Out, string Err)> Cli(params string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("systemd services are Linux only");
        }

        var output = new StringWriter();
        var error = new StringWriter();
        var code = await AgentdCli.InvokeAsync(args, output, error, () => throw new InvalidOperationException("no services needed"),
            (_, _) => Task.FromResult(0), CancellationToken.None, run: Run,
            environment: name => name switch { "XDG_CONFIG_HOME" => _config, "USER" => "tngo", "AGENTD_HOME" => _home, _ => null });
        return (code, output.ToString(), error.ToString());
    }

    private Task<ProcessResult> Run(string file, IReadOnlyList<string> args, bool interactive, CancellationToken ct)
    {
        var call = $"{file} {string.Join(' ', args)}";
        _calls.Add(call);
        return Task.FromResult(_results.TryGetValue(call, out var r) ? r : new ProcessResult(0, string.Empty));
    }
}
