using System.Text.Json.Nodes;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Application.Setup;
using Agentd.Host.Cli;
using Agentd.Host.Cli.Commands;
using Agentd.Host.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Host.Tests.Cli;

[TestClass]
public sealed class InitCommandTests : IDisposable
{
    private const string Repo = "git@ssh.dev.azure.com:v3/myorg/Portal/sysmin";

    private readonly ConfigHome _home = new ConfigHome(Directory.CreateTempSubdirectory("agentd-init-").FullName).EnsureCreated();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly Queue<string> _answers = new();
    private readonly Queue<string> _secrets = new();
    private readonly Dictionary<string, string> _env = [];
    private readonly Probes _probes = new();

    [TestMethod]
    public async Task Interactive_init_saves_every_step_and_finishes()
    {
        _secrets.Enqueue("Host=db;Username=agentd;Password=pw-SECRET;Database=agentd");
        Answer("", "myorg", "Portal", "Pat");                    // migrate: yes; Azure DevOps
        _secrets.Enqueue("pat-SECRET-0123456789");
        Answer("");                                              // generate the SSH key: yes
        _secrets.Enqueue("sk-ant-oat01-SECRET-0123456789");
        Answer("n", Repo, "", "");                               // no Discord; one repository; finish: yes

        Assert.AreEqual(ExitCodes.Ok, await RunAsync("init"), _err.ToString());

        var json = JsonNode.Parse(File.ReadAllText(_home.ConfigFile))!;
        Assert.AreEqual("myorg", json["AzureDevOps"]!["Organization"]!.GetValue<string>());
        Assert.AreEqual("Pat", json["AzureDevOps"]!["Auth"]!.GetValue<string>());
        Assert.AreEqual(Repo, json["Repositories"]!["Items"]![0]!["Url"]!.GetValue<string>());
        Assert.IsNotNull(json["Setup"]!["CompletedAt"]);
        Assert.IsTrue(SetupState.IsCompleteIn(_home));
        var stored = new SecretStore(_home).Load().Values;
        Assert.AreEqual("pat-SECRET-0123456789", stored["AzureDevOps:Pat"]);
        Assert.AreEqual("sk-ant-oat01-SECRET-0123456789", stored["Claude:OAuthToken"]);
        Assert.IsTrue(_probes.Migrated);
        Assert.IsTrue(_probes.Revoked);
        Assert.Contains("ssh-ed25519 AAAA agentd@test", _out.ToString());
        Assert.Contains("agentd daemon restart", _out.ToString());
        Assert.DoesNotContain("SECRET", _out.ToString() + _err.ToString());
        Assert.DoesNotContain("SECRET", File.ReadAllText(_home.ConfigFile));
    }

    [TestMethod]
    public async Task Enter_keeps_what_is_saved()
    {
        new SecretStore(_home).Set("ConnectionStrings:agentd", "Host=db;Password=x", "tngo");
        File.WriteAllText(_home.ConfigFile, """{ "AzureDevOps": { "Organization": "myorg", "Project": "Portal", "Auth": "AzCli" } }""");
        Answer("", "", "", "", "", "n", "", "n");               // migrate, org, project, auth, key, no Discord, no repo, don't finish

        Assert.AreEqual(ExitCodes.Ok, await RunAsync("init"), _err.ToString());

        Assert.Contains("[myorg]", string.Join('\n', _probes.Prompts));
        Assert.AreEqual("Portal", JsonNode.Parse(File.ReadAllText(_home.ConfigFile))!["AzureDevOps"]!["Project"]!.GetValue<string>());
        Assert.IsFalse(SetupState.IsCompleteIn(_home), "not finished when declined");
        Assert.Contains("Run `agentd init` again to finish.", _out.ToString());
    }

    [TestMethod]
    public async Task Non_interactive_lists_everything_missing()
    {
        Assert.AreEqual(ExitCodes.Usage, await RunAsync("init", "--non-interactive", "--auth", "Pat"));

        var error = _err.ToString();
        Assert.Contains(InitCommand.DatabaseVariable, error);
        Assert.Contains("--organization", error);
        Assert.Contains("--project", error);
        Assert.Contains(InitCommand.PatVariable, error);
        Assert.IsFalse(File.Exists(_home.ConfigFile), "nothing written");
    }

    [TestMethod]
    public async Task Non_interactive_takes_secrets_from_the_environment_only()
    {
        _env[InitCommand.DatabaseVariable] = "Host=db;Password=pw-SECRET";
        _env[InitCommand.PatVariable] = "pat-SECRET-0123456789";

        var exit = await RunAsync("init", "--non-interactive", "--organization", "https://dev.azure.com/myorg", "--project", "Portal", "--auth", "Pat", "--repo", Repo, "--generate-ssh-key");

        Assert.AreEqual(ExitCodes.Ok, exit, _err.ToString());
        Assert.IsTrue(SetupState.IsCompleteIn(_home));
        Assert.AreEqual("pat-SECRET-0123456789", new SecretStore(_home).Load().Values["AzureDevOps:Pat"]);
        Assert.IsTrue(_probes.KeyGenerated);
        Assert.IsEmpty(_probes.Prompts, "no prompts");
        Assert.DoesNotContain("SECRET", _out.ToString() + _err.ToString());
    }

    [TestMethod]
    public async Task A_failing_required_check_stops_before_finishing()
    {
        _probes.ClaudeFails = true;
        _env[InitCommand.DatabaseVariable] = "Host=db;Password=pw";

        var exit = await RunAsync("init", "--non-interactive", "--organization", "myorg", "--project", "Portal", "--auth", "AzCli");

        Assert.AreEqual(ExitCodes.Error, exit);
        Assert.Contains("❌ Claude", _out.ToString());
        Assert.IsFalse(SetupState.IsCompleteIn(_home));
        Assert.IsFalse(_probes.Revoked);
    }

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        Directory.Delete(_home.Root, recursive: true);
    }

    private void Answer(params string[] answers)
    {
        foreach (var a in answers)
        {
            _answers.Enqueue(a);
        }
    }

    private Task<int> RunAsync(params string[] args)
    {
        var services = new ServiceCollection()
            .AddSingleton<IConfigWriter>(new AgentdJsonFile(_home))
            .AddSingleton<ISecrets>(new StoredSecrets(new SecretStore(_home)))
            .AddSingleton<ISettingsAudit>(new FileSettingsAudit(_home, NullLogger<FileSettingsAudit>.Instance))
            .AddSingleton<IDatabaseProbe>(_probes).AddSingleton<IAzureDevOpsProbe>(_probes).AddSingleton<IGitKey>(_probes)
            .AddSingleton<IClaudeProbe>(_probes).AddSingleton<IChatProbe>(_probes).AddSingleton<IGitRemote>(_probes).AddSingleton<ISetupLink>(_probes)
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(new JobOptions())).AddSingleton(TimeProvider.System).AddSingleton<SetupService>()
            .BuildServiceProvider();
        return AgentdCli.InvokeAsync(args, _out, _err, () => services, (_, _) => Task.FromResult(0), CancellationToken.None,
            home: () => _home,
            readSecret: prompt => { _probes.Prompts.Add(prompt); return _secrets.TryDequeue(out var s) ? s : ""; },
            environment: name => _env.GetValueOrDefault(name),
            readLine: prompt => { _probes.Prompts.Add(prompt); return _answers.TryDequeue(out var a) ? a : null; });
    }

    /// <summary>Every outside check, answering like a working server.</summary>
    private sealed class Probes : IDatabaseProbe, IAzureDevOpsProbe, IGitKey, IClaudeProbe, IChatProbe, IGitRemote, ISetupLink
    {
        private GitKeyInfo? _key;

        public List<string> Prompts { get; } = [];

        public bool Migrated { get; private set; }

        public bool Revoked { get; private set; }

        public bool KeyGenerated => _key is not null;

        public bool ClaudeFails { get; set; }

        public string? Problem(string connectionString) => connectionString.Contains('=', StringComparison.Ordinal) ? null : "not a connection string";

        public Task<StepCheck> TestAsync(string connectionString, CancellationToken cancellationToken) =>
            Task.FromResult(Migrated ? new StepCheck(true, "Connected. The schema is up to date.") : new StepCheck(true, "Connected. The agentd schema isn't created yet.", "run the migrations"));

        public Task<StepCheck> MigrateAsync(string connectionString, CancellationToken cancellationToken)
        {
            Migrated = true;
            return Task.FromResult(new StepCheck(true, "Applied 17 migration(s)."));
        }

        public Task<StepCheck> TestAsync(AzureDevOpsConnection connection, JobOptions jobs, CancellationToken cancellationToken) => Task.FromResult(new StepCheck(true, "Signed in."));

        public GitKeyInfo? Read() => _key;

        public Task<GitKeyInfo> GenerateAsync(string comment, CancellationToken cancellationToken) =>
            Task.FromResult(_key = new GitKeyInfo("/k/id_ed25519", "ssh-ed25519 AAAA agentd@test", "SHA256:abc"));

        Task<StepCheck> IGitKey.TestAsync(string url, CancellationToken cancellationToken) => Task.FromResult(new StepCheck(true, "Reached."));

        public Task<ClaudeLogin> StatusAsync(CancellationToken cancellationToken) => Task.FromResult(new ClaudeLogin(true, "2.1.300", false, null, null));

        Task<StepCheck> IClaudeProbe.TestAsync(string? token, CancellationToken cancellationToken) =>
            Task.FromResult(ClaudeFails ? new StepCheck(false, "The test prompt failed.", "claude auth login") : new StepCheck(true, "Claude answered."));

        public Task<StepCheck> TestProfileAsync(string name, ModelProfile profile, CancellationToken cancellationToken) => Task.FromResult(new StepCheck(true, "Answered."));

        public Task<StepCheck> TestAsync(ChatConnection connection, CancellationToken cancellationToken) => Task.FromResult(new StepCheck(true, "Posted."));

        public Task<string> GetDefaultBranchAsync(string remoteUrl, CancellationToken cancellationToken) => Task.FromResult("develop");

        public void Revoke() => Revoked = true;
    }
}
