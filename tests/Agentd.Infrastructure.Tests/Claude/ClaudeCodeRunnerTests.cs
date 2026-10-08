using System.Collections;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Infrastructure.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Tests.Claude;

[TestClass]
[DoNotParallelize]   // one test sets a process environment variable
[OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]   // uses a bash fake of the claude CLI
public sealed class ClaudeCodeRunnerTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init() => _dir = Directory.CreateTempSubdirectory("agentd-claude-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    [TestMethod]
    public void A_turns_step_model_and_effort_replace_the_default_model()
    {
        var options = new ClaudeOptions { Model = "sonnet" };

        var stepped = ClaudeArgs.Build(Request(resume: true) with { Model = "opus", Effort = "high" }, options, null).ToList();
        var plain = ClaudeArgs.Build(Request(resume: true), options, null).ToList();

        Assert.AreEqual("opus", stepped[stepped.IndexOf("--model") + 1]);
        Assert.AreEqual("high", stepped[stepped.IndexOf("--effort") + 1]);
        Assert.AreEqual(1, stepped.Count(a => a == "--model"));
        Assert.AreEqual("sonnet", plain[plain.IndexOf("--model") + 1]);
        Assert.DoesNotContain("--effort", plain);
    }

    [TestMethod]
    public async Task A_run_streams_the_transcript_records_events_and_returns_the_summary()
    {
        var (runner, events, _) = Runner(Script("cat \"$FIXTURE\""));

        var outcome = await runner.RunAsync(Request(resume: false), default);

        var exited = (AgentRunOutcome.Exited)outcome;
        Assert.AreEqual(0, exited.ExitCode);
        Assert.AreEqual(1, exited.Summary!.Turns);
        CollectionAssert.IsSubsetOf(new[] { "agent.session", "agent.text", "agent.rate_limit", "agent.result" }, events.Types.ToList());
        var transcript = Path.Combine(_dir, "logs", "wi-1234", "transcript.jsonl");
        Assert.HasCount(4, File.ReadAllLines(transcript));
        Assert.AreEqual(0.01, _activity.Get(new JobId(42)).Usage?.FiveHour, "usage feeds the heartbeat and warnings");
    }

    [TestMethod]
    public async Task Arguments_use_session_id_first_and_resume_later_with_safe_values()
    {
        var (runner, _, _) = Runner(Script("printf '%s\\n' \"$@\" > \"$OUT/args.txt\"; cat \"$FIXTURE\""));
        var request = Request(resume: false);

        await runner.RunAsync(request, default);
        var first = File.ReadAllLines(Path.Combine(_dir, "args.txt"));
        await runner.RunAsync(request with { Resume = true, Prompt = "continue" }, default);
        var second = File.ReadAllLines(Path.Combine(_dir, "args.txt"));

        CollectionAssert.Contains(first, "--session-id");
        CollectionAssert.Contains(first, request.Session.ToString());
        CollectionAssert.Contains(first, "Fix the login redirect; don't break `$HOME` or \"quotes\"");
        CollectionAssert.Contains(second, "--resume");
        CollectionAssert.Contains(first, "stream-json");
        Assert.AreEqual("--allowedTools", first[Array.IndexOf(first, "--allowedTools")]);
    }

    [TestMethod]
    public void A_read_only_turn_gets_only_reading_tools_and_edits_are_denied()
    {
        var options = new ClaudeOptions();
        var readOnly = Request(resume: false) with { ReadOnly = true };

        var args = ClaudeArgs.Build(readOnly, options, null).ToList();
        var normal = ClaudeArgs.Build(Request(resume: false), options, null).ToList();

        var allowed = args[args.IndexOf("--allowedTools") + 1].Split(',');
        CollectionAssert.DoesNotContain(allowed, "Edit");
        CollectionAssert.DoesNotContain(allowed, "Bash(git commit:*)");
        CollectionAssert.DoesNotContain(allowed, "Bash(dotnet build:*)");
        CollectionAssert.Contains(allowed, "Read");
        Assert.AreEqual("Edit,Write,MultiEdit,NotebookEdit", args[args.IndexOf("--disallowedTools") + 1]);
        CollectionAssert.Contains(normal[normal.IndexOf("--allowedTools") + 1].Split(','), "Bash(dotnet test:*)");
        CollectionAssert.DoesNotContain(normal, "--disallowedTools");
    }

    [TestMethod]
    public async Task The_agent_never_sees_agentd_secrets_or_an_api_key_and_stdin_is_closed()
    {
        Environment.SetEnvironmentVariable("Agentd__AzureDevOps__Pat", "must-not-leak");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "must-not-leak");
        try
        {
            var (runner, _, _) = Runner(Script("env > \"$OUT/env.txt\"; if read -t 2 x; then echo open > \"$OUT/stdin.txt\"; else echo closed > \"$OUT/stdin.txt\"; fi; cat \"$FIXTURE\""));

            await runner.RunAsync(Request(resume: false), default);

            var env = File.ReadAllText(Path.Combine(_dir, "env.txt"));
            Assert.DoesNotContain("must-not-leak", env);
            Assert.AreEqual("closed", File.ReadAllText(Path.Combine(_dir, "stdin.txt")).Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("Agentd__AzureDevOps__Pat", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }

    [TestMethod]
    public async Task A_rejected_rate_limit_returns_UsageLimited_with_the_reset_time()
    {
        var (runner, _, _) = Runner(Script("""
            echo '{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1790752800}}'
            echo '{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":0}'
            exit 1
            """));

        var outcome = await runner.RunAsync(Request(resume: false), default);

        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1790752800), ((AgentRunOutcome.UsageLimited)outcome).ResetAt);
    }

    [TestMethod]
    public async Task No_output_for_the_idle_timeout_kills_the_process()
    {
        var (runner, _, _) = Runner(Script("echo '{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\"}'; sleep 30"), idle: TimeSpan.FromSeconds(1));
        var started = DateTimeOffset.UtcNow;

        var outcome = await runner.RunAsync(Request(resume: false), default);

        Assert.IsInstanceOfType<AgentRunOutcome.TimedOut>(outcome);
        Assert.IsLessThan(TimeSpan.FromSeconds(15), DateTimeOffset.UtcNow - started);
    }

    [TestMethod]
    public async Task Cancel_kills_a_running_agent()
    {
        var (runner, _, _) = Runner(Script("echo '{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\"}'; sleep 30"));
        var request = Request(resume: false);

        var run = runner.RunAsync(request, default);
        while (!runner.IsRunning(request.JobId))
        {
            await Task.Delay(20);
        }

        runner.Cancel(request.JobId);
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.IsInstanceOfType<AgentRunOutcome.Cancelled>(outcome);
        Assert.IsFalse(runner.IsRunning(request.JobId));
    }

    [TestMethod]
    public async Task The_mcp_config_carries_a_bearer_token_is_private_and_is_deleted_afterwards()
    {
        var (runner, _, tokens) = Runner(
            Script("cfg=\"\"; while [ $# -gt 0 ]; do if [ \"$1\" = --mcp-config ]; then cfg=\"$2\"; fi; shift; done; cp \"$cfg\" \"$OUT/mcp-copy.json\"; stat -c %a \"$cfg\" > \"$OUT/mode.txt\"; cat \"$FIXTURE\""),
            mcpUrl: "http://127.0.0.1:7780/mcp");

        await runner.RunAsync(Request(resume: false), default);

        StringAssert.Contains(File.ReadAllText(Path.Combine(_dir, "mcp-copy.json")), "Bearer token-for-42");
        Assert.AreEqual("600", File.ReadAllText(Path.Combine(_dir, "mode.txt")).Trim());
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "logs", "wi-1234", "mcp.json")), "deleted after the run");
        Assert.AreEqual(42L, tokens.Revoked.Single());
    }

    [TestMethod]
    public void Safe_environment_removes_infrastructure_keys_and_adds_subscription_auth()
    {
        var current = new Hashtable
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/agentd",
            ["Agentd__Discord__BotToken"] = "x",
            ["ConnectionStrings__agentd"] = "x",
            ["ANTHROPIC_API_KEY"] = "x",
            ["AZURE_CLIENT_SECRET"] = "x",
        };

        var env = SafeEnvironment.Build(current, new ClaudeOptions { ConfigDir = "/opt/agentd/claude/max-1", OAuthToken = "oauth" });

        CollectionAssert.AreEquivalent(new[] { "PATH", "HOME", "CLAUDE_CONFIG_DIR", "CLAUDE_CODE_OAUTH_TOKEN", "MCP_TOOL_TIMEOUT", "BASH_MAX_TIMEOUT_MS" }, env.Keys.ToList());
        Assert.AreEqual("600000", env["BASH_MAX_TIMEOUT_MS"], "a hung command ends after 10 minutes (Claude:CommandTimeout)");
        Assert.AreEqual("900000", env["MCP_TOOL_TIMEOUT"], "15 minutes: longer than the permission timeout");
        Assert.AreEqual("/opt/agentd/claude/max-1", env["CLAUDE_CONFIG_DIR"]);
    }

    private string Script(string body)
    {
        var path = Path.Combine(_dir, $"fake-claude-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/usr/bin/env bash\nOUT=\"{_dir}\"\nFIXTURE=\"{Path.Combine(AppContext.BaseDirectory, "Claude", "Fixtures", "simple-text.jsonl")}\"\n{body}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private readonly Application.Jobs.JobActivity _activity = new();

    private (ClaudeCodeRunner Runner, RecordingEvents Events, FakeTokens Tokens) Runner(string binary, TimeSpan? idle = null, string? mcpUrl = null)
    {
        var options = new ClaudeOptions { Binary = binary, TranscriptRoot = Path.Combine(_dir, "logs"), IdleTimeout = idle ?? TimeSpan.FromMinutes(1), McpUrl = mcpUrl };
        var events = new RecordingEvents();
        var tokens = new FakeTokens();
        return (new ClaudeCodeRunner(Options.Create(options), events, tokens, _activity, NullLogger<ClaudeCodeRunner>.Instance), events, tokens);
    }

    private AgentRunRequest Request(bool resume) =>
        new(new JobId(42), WorkItemId.From(1234), new WorktreePath(_dir), new ClaudeSessionId(Guid.Parse("11111111-2222-3333-4444-555555555555")),
            "Fix the login redirect; don't break `$HOME` or \"quotes\"", resume);

    private sealed class RecordingEvents : IEventStore
    {
        public List<string> Types { get; } = [];

        public Task<long> AppendAsync(JobId? jobId, string type, string payloadJson, CancellationToken cancellationToken)
        {
            lock (Types)
            {
                Types.Add(type);
                return Task.FromResult((long)Types.Count);
            }
        }
    }

    private sealed class FakeTokens : IMcpTokenIssuer
    {
        public List<long> Revoked { get; } = [];

        public string Issue(JobId jobId) => $"token-for-{jobId}";

        public JobId? Validate(string token) => null;

        public void Revoke(JobId jobId) => Revoked.Add(jobId.Value);

        public string IssueChat(long chatId) => $"chat-token-{chatId}";

        public long? ValidateChat(string token) => null;

        public void RevokeChat(long chatId)
        {
        }
    }
}
