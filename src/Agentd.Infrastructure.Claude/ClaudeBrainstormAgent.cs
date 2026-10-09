using System.Diagnostics;
using System.Globalization;
using System.Text;
using Agentd.Application.Ideas;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Claude;

/// <summary>
/// Brainstorm turns with Claude Code: read-only tools only (no edits, no agentd MCP tools, no MCP at all), in the
/// idea's detached checkout; <c>--model</c> / <c>--effort</c> from the idea. The reply is the session's final message.
/// </summary>
public sealed class ClaudeBrainstormAgent(IOptions<ClaudeOptions> options) : IBrainstormAgent
{
    public const int MaxTurns = 40;

    public const string Rules =
        "You are agentd's brainstorming partner in a chat thread. A developer brings an idea; help them shape it into well-formed " +
        "Azure DevOps work items. Your working directory is a read-only checkout of the repository: read and search it (Read, Grep, Glob, " +
        "git log/show) so your answers point at real files, but never try to edit, build or commit. " +
        "Ask clarifying questions, offer options with trade-offs, and size the work roughly. Keep each reply short (under ~250 words), " +
        "in the developer's language, and end with one clear question or next step. " +
        "When the goal is clear (or the developer asks), propose work items by ending your message with a fenced block:\n" +
        "```work-items\n[{\"type\":\"User Story\",\"title\":\"…\",\"description\":\"…\",\"acceptanceCriteria\":\"…\",\"estimate\":3,\"tags\":[]}," +
        "{\"type\":\"Task\",\"title\":\"…\",\"description\":\"…\",\"estimate\":4,\"parent\":0}]\n```\n" +
        "type is \"User Story\" or \"Task\"; estimate is story points for stories and hours for tasks; parent is the index of the task's story " +
        "in the list. At most 10 items. When the developer asks for changes, send the whole revised block.";

    /// <summary>Instructions for a finished job's follow-up turn (<see cref="ThreadTurnKind.FollowUp"/>): talk only.</summary>
    public const string FollowUpRules =
        "The pull request for this work item is merged and the job is finished. You're answering the developer in the job's chat " +
        "thread, read-only: you may read and search the code (the checkout now holds the base branch, with your merged work), but never " +
        "edit, build, commit, push or open anything, and agentd's job tools are gone. Answer questions about what you did and why, " +
        "briefly (under ~250 words). If they ask for changes, say what you would change and where, and tell them to start a new run " +
        "with `!run <work item id>` or create a work item: no code changes happen in this thread anymore.";

    /// <summary>Instructions for a PR review turn (<see cref="ThreadTurnKind.Review"/>).</summary>
    public const string ReviewRules =
        "You are agentd's code reviewer, talking with a developer in a chat thread. Your working directory is a read-only, detached " +
        "checkout of the pull request's head. The prompt gives the source and target branches, the PR description, the linked work " +
        "items with their acceptance criteria, and the PR's open comment threads. Never edit, build, commit or push. Reply in the " +
        "developer's language.\n\n" +
        "Report ONLY two kinds of problems:\n" +
        "- breaks: it can break the app in use: a bug or wrong result, a crash or unhandled error, data loss or corruption, a " +
        "security hole (injection, missing authorization, secrets in the diff), a breaking change (public API, contract, configuration " +
        "key, a migration without a safe rollback), or an acceptance criterion of the linked work item that isn't met.\n" +
        "- performance: it makes the app measurably slower or heavier: N+1 queries, unbounded loops or memory, blocking calls on hot " +
        "paths, missing pagination or indexes for data that grows.\n" +
        "Leave everything else out: style, naming, formatting, comments, docs, missing tests, refactors, \"could be cleaner\". If it can't " +
        "break the app or slow it down, it isn't a finding.\n\n" +
        "How to review:\n" +
        "1. Read the PR description and the linked work items, then the change: git diff origin/<target>...HEAD, git log " +
        "origin/<target>..HEAD. Read the code around each change, not just the diff. When the developer names a focus, start there.\n" +
        "2. Verify before you claim: search for callers, read the code paths, and only report what you could confirm. If you aren't " +
        "sure it breaks, leave it out. Skip anything already raised in the open comment threads.\n\n" +
        "Findings: when you have them, and again whenever they change, end your message with a fenced block:\n" +
        "```review-findings\n{\"summary\":\"what the PR does and your verdict (ready or needs changes), one or two sentences\",\"findings\":[" +
        "{\"severity\":\"breaks\",\"file\":\"src/Foo/Bar.cs\",\"line\":42,\"title\":\"what breaks, in at most 12 words\"," +
        "\"detail\":\"why, in one or two short sentences\",\"suggestion\":\"the concrete fix, in one or two sentences (a short code " +
        "snippet is fine)\"}]}\n```\n" +
        "severity is breaks or performance. file is relative to the repository root and line is on the PR's side; omit both for a " +
        "PR-wide finding. At most 15 findings, worst first. An empty list is right when nothing can break or slow down.\n\n" +
        "Outside the block keep replies short (under ~150 words). In follow-ups, answer the developer's questions; if they convince you " +
        "a finding is wrong, or ask you to change the list, send the whole revised block. Never claim you approved, posted or merged " +
        "anything: agentd posts only the findings the developer chooses.";

    /// <summary>Instructions for a <c>!chat</c> turn (<see cref="ThreadTurnKind.Chat"/>).</summary>
    public const string ChatRules =
        "You are agentd's assistant in a chat thread, answering a developer's questions about the team's repositories. Your working " +
        "directory and the added directories are read-only checkouts of each repository's base branch (the first prompt lists them). " +
        "Read and search them (Read, Grep, Glob, git log/show/blame) and answer from what you find: name the files and lines " +
        "(`path:line`) so the developer can check. For Azure DevOps use agentd's tools: ado_search_work_items, ado_get_work_item, " +
        "ado_list_pull_requests, ado_get_pull_request, and for pipelines ado_list_pipelines, ado_list_builds, ado_get_build (a failed run's " +
        "errors and log end: say which step failed and why, then point at the code) (read-only; link items as #id and PRs as !id). " +
        "Never edit, build, commit or push. If the " +
        "answer isn't in the code or Azure DevOps, say so plainly " +
        "instead of guessing. Keep answers short (under ~250 words) unless asked for detail, in the developer's language. When " +
        "something should become work, suggest `!idea <text>` (to shape work items) or `!run <work item id>`.";

    public static IReadOnlyList<string> Args(BrainstormTurn turn, ClaudeOptions o, string? mcpConfig = null)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(o);
        var args = new List<string>
        {
            "-p", turn.Prompt,
            turn.Resume ? "--resume" : "--session-id", turn.Session.ToString(),
            "--output-format", "stream-json", "--verbose",
            "--max-turns", MaxTurns.ToString(CultureInfo.InvariantCulture),
            "--append-system-prompt", turn.Kind switch { ThreadTurnKind.Review => ReviewRules, ThreadTurnKind.FollowUp => FollowUpRules, ThreadTurnKind.Chat => ChatRules, _ => Rules },
            "--strict-mcp-config",
            // agentd's tools only for a turn that brings its own token (a chat); the rest run with no MCP at all.
            "--allowedTools", string.Join(",", o.ReadOnlyTools.Where(t => turn.McpToken is not null || !t.StartsWith("mcp__", StringComparison.Ordinal))),
            "--disallowedTools", "Edit,Write,MultiEdit,NotebookEdit",
        };
        if ((turn.Model ?? o.Model) is { Length: > 0 } model)
        {
            args.AddRange(["--model", model]);
        }

        if (turn.Effort is { Length: > 0 } effort)
        {
            args.AddRange(["--effort", effort]);
        }

        if (turn.McpToken is not null && mcpConfig is { } config)
        {
            args.AddRange(["--mcp-config", config]);
        }

        // One flag per directory: --add-dir takes a list and would swallow what follows.
        foreach (var dir in turn.AddDirs ?? [])
        {
            args.AddRange(["--add-dir", dir]);
        }

        return args;
    }

    public async Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var o = options.Value;
        var dir = Path.Combine(Paths.Expand(o.TranscriptRoot), $"{turn.Kind switch { ThreadTurnKind.Review => "review", ThreadTurnKind.FollowUp => "followup", ThreadTurnKind.Chat => "chat", _ => "idea" }}-{turn.IdeaId}");
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo(o.Binary)
        {
            WorkingDirectory = turn.Worktree,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in Args(turn, o, WriteMcpConfig(turn, dir, o)))
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment.Clear();
        foreach (var (key, value) in SafeEnvironment.Build(Environment.GetEnvironmentVariables(), o))
        {
            psi.Environment[key] = value;
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{o.Binary}'.");
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var tracker = new RunTracker();
        string? result = null;
        var failed = false;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(o.IdleTimeout);
        try
        {
            await using var transcript = new StreamWriter(Path.Combine(dir, "transcript.jsonl"), append: true, Encoding.UTF8);
            while (await process.StandardOutput.ReadLineAsync(idle.Token).ConfigureAwait(false) is { } line)
            {
                idle.CancelAfter(o.IdleTimeout);
                await transcript.WriteLineAsync(line).ConfigureAwait(false);
                foreach (var agentEvent in StreamJsonParser.Parse(line))
                {
                    tracker.Observe(agentEvent);
                    if (agentEvent is AgentEvent.TurnResult r)
                    {
                        result = r.Result;
                        failed = r.IsError;
                    }
                }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new BrainstormReply(null, null, cancellationToken.IsCancellationRequested ? "cancelled" : "no output for too long");
        }

        if (tracker.UsageLimited)
        {
            return new BrainstormReply(null, tracker.LimitResetsAt ?? DateTimeOffset.UtcNow.AddHours(1), null);
        }

        var error = (await stderr.ConfigureAwait(false)).Trim();
        return !failed && !string.IsNullOrWhiteSpace(result)
            ? new BrainstormReply(result, null, null)
            : new BrainstormReply(null, null, failed ? $"the agent stopped with an error{(result is null ? string.Empty : ": " + result)}" : error.Length > 0 ? error[..Math.Min(200, error.Length)] : $"exit code {process.ExitCode}");
    }

    /// <summary>A chat's agentd MCP server, with its bearer token (0600, like a job's).</summary>
    private static string? WriteMcpConfig(BrainstormTurn turn, string dir, ClaudeOptions o)
    {
        if (turn.McpToken is null || string.IsNullOrWhiteSpace(o.McpUrl))
        {
            return null;
        }

        var path = Path.Combine(dir, "mcp.json");
        var config = new System.Text.Json.Nodes.JsonObject
        {
            ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
            {
                ["agentd"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "http",
                    ["url"] = o.McpUrl,
                    ["headers"] = new System.Text.Json.Nodes.JsonObject { ["Authorization"] = "Bearer " + turn.McpToken },
                },
            },
        };
        File.WriteAllText(path, config.ToJsonString());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
