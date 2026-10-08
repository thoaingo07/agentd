using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Infrastructure.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Tests.Claude;

/// <summary>
/// Runs the real Claude Code CLI (the machine's subscription) once with a trivial prompt in an empty temp
/// folder, to prove the runner's exact flags are accepted. Opt-in: AGENTD_LIVE_CLAUDE=1. Uses a tiny amount of quota.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class LiveClaudeTests
{
    [TestMethod]
    public async Task Real_cli_accepts_the_runner_flags_and_reports_a_result()
    {
        if (Environment.GetEnvironmentVariable("AGENTD_LIVE_CLAUDE") != "1")
        {
            Assert.Inconclusive("Set AGENTD_LIVE_CLAUDE=1 to run the real Claude CLI.");
            return;
        }

        var dir = Directory.CreateTempSubdirectory("agentd-live-claude-").FullName;
        try
        {
            var options = new ClaudeOptions { TranscriptRoot = Path.Combine(dir, "logs"), MaxTurns = 1, IdleTimeout = TimeSpan.FromMinutes(2) };
            options.AllowedTools.Clear();
            options.AllowedTools.Add("Read");
            var runner = new ClaudeCodeRunner(Options.Create(options), new NullEvents(), new NullTokens(), new Application.Jobs.JobActivity(), NullLogger<ClaudeCodeRunner>.Instance);
            var request = new AgentRunRequest(new JobId(1), WorkItemId.From(1), new WorktreePath(dir), ClaudeSessionId.New(), "Reply with exactly: pong", Resume: false);

            var outcome = await runner.RunAsync(request, default);

            var exited = (AgentRunOutcome.Exited)outcome;
            Assert.AreEqual(0, exited.ExitCode);
            Assert.IsFalse(exited.Summary!.IsError);
            StringAssert.Contains(File.ReadAllText(Path.Combine(dir, "logs", "wi-1", "transcript.jsonl")), "pong");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class NullEvents : IEventStore
    {
        public Task<long> AppendAsync(JobId? jobId, string type, string payloadJson, CancellationToken cancellationToken) => Task.FromResult(0L);
    }

    private sealed class NullTokens : IMcpTokenIssuer
    {
        public string Issue(JobId jobId) => string.Empty;

        public JobId? Validate(string token) => null;

        public void Revoke(JobId jobId)
        {
        }

        public string IssueChat(long chatId) => $"chat-token-{chatId}";

        public long? ValidateChat(string token) => null;

        public void RevokeChat(long chatId)
        {
        }
    }
}
