using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Host.Cli;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Host.Tests.Cli;

[TestClass]
public sealed class AgentdCliTests : IDisposable
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly List<string[]> _daemonRuns = [];
    private readonly FakeClaim _claim = new();

    [TestMethod]
    public async Task Help_lists_every_verb()
    {
        Assert.AreEqual(0, await Run("--help"));

        foreach (var verb in new[] { "daemon", "status", "run", "repo", "db", "doctor", "secrets" })
        {
            Assert.Contains($"  {verb}", _out.ToString(), $"help lists '{verb}'");
        }
    }

    [TestMethod]
    [DataRow("status")]
    [DataRow("run")]
    [DataRow("repo")]
    [DataRow("repo", "add")]
    [DataRow("repo", "list")]
    [DataRow("repo", "remove")]
    [DataRow("db", "migrate")]
    [DataRow("doctor")]
    [DataRow("daemon", "run")]
    [DataRow("secrets", "set")]
    [DataRow("secrets", "list")]
    [DataRow("secrets", "remove")]
    public async Task Every_verb_has_help(params string[] verb)
    {
        Assert.AreEqual(0, await Run([.. verb, "--help"]));

        Assert.Contains("Usage:", _out.ToString());
    }

    [TestMethod]
    [DataRow("bogus")]
    [DataRow("daemon")]
    [DataRow("run")]
    [DataRow("run", "not-a-number")]
    [DataRow("status", "--nope")]
    public async Task Usage_errors_exit_with_2(params string[] args)
    {
        Assert.AreEqual(ExitCodes.Usage, await Run(args));

        Assert.Contains("agentd --help", _err.ToString());
    }

    [TestMethod]
    public async Task No_arguments_runs_the_daemon()
    {
        Assert.AreEqual(0, await Run());

        Assert.HasCount(1, _daemonRuns);
    }

    [TestMethod]
    public async Task Host_options_without_a_verb_run_the_daemon()
    {
        Assert.AreEqual(0, await Run("--urls", "http://127.0.0.1:9999"));

        CollectionAssert.AreEqual(new[] { "--urls", "http://127.0.0.1:9999" }, _daemonRuns.Single());
    }

    [TestMethod]
    public async Task Daemon_run_passes_extra_arguments_to_the_web_host()
    {
        Assert.AreEqual(0, await Run("daemon", "run", "--urls", "http://127.0.0.1:9999"));

        CollectionAssert.AreEqual(new[] { "--urls", "http://127.0.0.1:9999" }, _daemonRuns.Single());
    }

    [TestMethod]
    public async Task Run_queues_a_job_then_refuses_a_second_active_job()
    {
        Assert.AreEqual(ExitCodes.Ok, await Run("run", "1234"));
        StringAssert.Contains(_out.ToString(), "Queued job #1 for work item #1234");

        Assert.AreEqual(ExitCodes.Conflict, await Run("run", "1234"));
        StringAssert.Contains(_err.ToString(), "already has active job");
        Assert.IsTrue(_claim.Commands.All(c => c.Force), "agentd run forces the claim");
    }

    [TestMethod]
    public async Task Run_passes_the_repository_override()
    {
        await Run("run", "1234", "--repo", "sysmin");

        Assert.AreEqual("sysmin", _claim.Commands.Single().Repository?.Value);
    }

    [TestMethod]
    public async Task Run_without_a_matching_repository_exits_with_3()
    {
        _claim.NoRepository = true;

        Assert.AreEqual(ExitCodes.NotFound, await Run("run", "1234"));
    }

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
    }

    private Task<int> Run(params string[] args)
    {
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<ClaimWorkItem, JobId>>(_claim)
            .BuildServiceProvider();
        return AgentdCli.InvokeAsync(args, _out, _err, () => services, (a, _) =>
        {
            _daemonRuns.Add(a);
            return Task.FromResult(0);
        }, CancellationToken.None);
    }

    private sealed class FakeClaim : ICommandHandler<ClaimWorkItem, JobId>
    {
        private readonly HashSet<int> _active = [];

        public List<ClaimWorkItem> Commands { get; } = [];

        public bool NoRepository { get; set; }

        public Task<Result<JobId>> Handle(ClaimWorkItem command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            Result<JobId> result = NoRepository
                ? DomainError.NotFound($"A repository for work item {command.WorkItemId}")
                : _active.Add(command.WorkItemId.Value)
                    ? new JobId(_active.Count)
                    : DomainError.Conflict($"Work item {command.WorkItemId} already has active job 1.");
            return Task.FromResult(result);
        }
    }
}
