using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Claude;

/// <summary>
/// Runs one Claude Code process per job turn in the job's worktree: streams stdout to the transcript
/// and the event log, enforces an idle timeout, supports cancel, and reports usage limits.
/// </summary>
public sealed partial class ClaudeCodeRunner(
    IOptions<ClaudeOptions> options,
    IEventStore events,
    IMcpTokenIssuer tokens,
    IAgentActivitySink activity,
    ILogger<ClaudeCodeRunner> logger,
    IOptions<Application.Jobs.ModelsOptions>? models = null) : IAgentRunner
{
    private readonly ConcurrentDictionary<long, Running> _running = new();

    public bool IsRunning(JobId jobId) => _running.ContainsKey(jobId.Value);

    public IReadOnlyDictionary<long, int> ProcessIds()
    {
        var ids = new Dictionary<long, int>();
        foreach (var (job, run) in _running)
        {
            try
            {
                ids[job] = run.Process.Id;
            }
            catch (InvalidOperationException)
            {
                // Not started or already gone.
            }
        }

        return ids;
    }

    public void Cancel(JobId jobId)
    {
        if (_running.TryGetValue(jobId.Value, out var run))
        {
            run.Cancelled = true;
            TryKill(run.Process);
        }
    }

    public async Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var o = options.Value;
        var jobDir = Path.Combine(Paths.Expand(o.TranscriptRoot), $"wi-{request.WorkItemId}");
        Directory.CreateDirectory(jobDir);
        var mcpConfig = WriteMcpConfig(request, jobDir, o);

        var psi = new ProcessStartInfo(o.Binary)
        {
            WorkingDirectory = request.Worktree.Value,
            RedirectStandardInput = true,     // closed immediately: the CLI otherwise waits for stdin
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in ClaudeArgs.Build(request, o, mcpConfig))
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment.Clear();
        var env = SafeEnvironment.Build(Environment.GetEnvironmentVariables(), o);
        if (request.Profile is { } name)
        {
            // Another provider (DeepSeek, …): its endpoint, key and models in this process only.
            var profile = (models is not null && models.Value.Profiles.TryGetValue(name, out var p) ? p : null) ?? throw new InvalidOperationException($"Agentd:Models:Profiles:{name} isn't configured.");
            ProfileEnvironment.Apply(env, name, profile, o);
        }

        foreach (var (key, value) in env)
        {
            psi.Environment[key] = value;
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{o.Binary}'.");
        process.StandardInput.Close();
        var run = new Running(process);
        _running[request.JobId.Value] = run;
        LogStarted(logger, request.JobId.Value, process.Id, request.Resume);

        var tracker = new RunTracker();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(o.IdleTimeout);
        var stderrTask = PumpStderrAsync(process, request.JobId.Value);
        try
        {
            await using var transcript = new StreamWriter(Path.Combine(jobDir, "transcript.jsonl"), append: true, Encoding.UTF8);
            while (await process.StandardOutput.ReadLineAsync(idle.Token).ConfigureAwait(false) is { } line)
            {
                idle.CancelAfter(o.IdleTimeout);   // restart the idle watchdog on every line
                await transcript.WriteLineAsync(line).ConfigureAwait(false);
                await transcript.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var agentEvent in StreamJsonParser.Parse(line))
                {
                    tracker.Observe(agentEvent);
                    ReportActivity(request.JobId, request.Session.Value.ToString(), agentEvent);
                    await events.AppendAsync(request.JobId, agentEvent.LogType, StreamJsonParser.ToPayloadJson(agentEvent), CancellationToken.None).ConfigureAwait(false);
                }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            LogIdle(logger, request.JobId.Value, o.IdleTimeout);
            return new AgentRunOutcome.TimedOut();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new AgentRunOutcome.Cancelled();
        }
        finally
        {
            _running.TryRemove(request.JobId.Value, out _);
            tokens.Revoke(request.JobId);
            if (mcpConfig is not null)
            {
                File.Delete(mcpConfig);
            }

            await stderrTask.ConfigureAwait(false);
        }

        if (run.Cancelled)
        {
            return new AgentRunOutcome.Cancelled();
        }

        if (tracker.UsageLimited)
        {
            return new AgentRunOutcome.UsageLimited(tracker.LimitResetsAt);
        }

        return new AgentRunOutcome.Exited(process.ExitCode, tracker.Summary);
    }

    private string? WriteMcpConfig(AgentRunRequest request, string jobDir, ClaudeOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.McpUrl))
        {
            return null;
        }

        var path = Path.Combine(jobDir, "mcp.json");
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["agentd"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = o.McpUrl,
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + tokens.Issue(request.JobId) },
                },
            },
        };
        File.WriteAllText(path, config.ToJsonString());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // contains a bearer token
        }

        return path;
    }

    private void ReportActivity(JobId jobId, string session, AgentEvent agentEvent)
    {
        switch (agentEvent)
        {
            case AgentEvent.ToolCall call:
                activity.ToolStep(jobId, ActivityText.Describe(call.Name, call.InputJson), DateTimeOffset.UtcNow);
                break;
            case AgentEvent.ToolResult:
                activity.ToolFinished(jobId);
                break;
            case AgentEvent.TaskStarted task:
                activity.CommandStarted(jobId, task.SessionId ?? session, task.TaskId);
                break;
            case AgentEvent.RateLimit limit:
                activity.Usage(jobId, limit.Utilization.GetValueOrDefault("five_hour", double.NaN) is var f && !double.IsNaN(f) ? f : null,
                    limit.Utilization.GetValueOrDefault("seven_day", double.NaN) is var w && !double.IsNaN(w) ? w : null, limit.ResetsAt);
                break;
        }
    }

    private async Task PumpStderrAsync(Process process, long jobId)
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                LogStderr(logger, jobId, line);
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private sealed class Running(Process process)
    {
        public Process Process { get; } = process;

        public volatile bool Cancelled;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId}: claude started (pid {Pid}, resume {Resume})")]
    private static partial void LogStarted(ILogger logger, long jobId, int pid, bool resume);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: no output for {Timeout}; killing claude")]
    private static partial void LogIdle(ILogger logger, long jobId, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} claude stderr: {Line}")]
    private static partial void LogStderr(ILogger logger, long jobId, string line);
}
