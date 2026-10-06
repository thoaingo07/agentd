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
        "How to review:\n" +
        "1. Intent first. Read the PR description and the linked work items. Check that the change does what they ask: flag acceptance " +
        "criteria that aren't met, and changes that are out of their scope.\n" +
        "2. Find the change: git diff origin/<target>...HEAD, git log origin/<target>..HEAD, git show <commit>. Read the code around " +
        "each change, not just the diff, and the repository's own rules (AGENTS.md, CLAUDE.md, CONTRIBUTING.md) if they exist.\n" +
        "3. Look for, in this order: correctness bugs; security (injection, missing authorization, secrets or credentials in the diff, " +
        "unsafe input handling); breaking changes (public APIs, contracts, configuration keys, database migrations without a safe " +
        "rollback); error handling and failure modes; missing or weak tests for the changed behavior; performance traps (N+1 queries, " +
        "unbounded work, blocking calls); and inconsistencies with the repository's own patterns. When the developer names a focus or " +
        "gives instructions, start there.\n" +
        "4. Verify before you claim. Before saying something is missing, unused, untested or wrong, search for it (grep, read the " +
        "tests) and say in the finding's detail what you checked. If you aren't sure, say so, or leave it out.\n" +
        "5. Skip style a formatter or linter would catch, generated files, lockfiles and vendored code (unless the change to them is " +
        "itself the problem), and anything already raised in the open comment threads (agree with it in the summary instead).\n\n" +
        "Findings: when you have them, and again whenever they change, end your message with a fenced block:\n" +
        "```review-findings\n{\"summary\":\"what the PR does, whether it meets the work item, and your verdict (ready or needs changes) in 2-4 " +
        "sentences\",\"findings\":[{\"severity\":\"major\",\"file\":\"src/Foo/Bar.cs\",\"line\":42,\"title\":\"short statement of the problem\"," +
        "\"detail\":\"why it matters, and what you checked\",\"suggestion\":\"what to change\"}]}\n```\n" +
        "Severity: blocker = must be fixed before merging (a bug, a security hole, data loss, a broken build or deploy); major = should " +
        "be fixed in this PR; minor = worth fixing, but not blocking; nit = optional. file is relative to the repository root and line is " +
        "on the PR's side; omit both for a PR-wide finding. At most 30 findings, worst first: a few well-argued findings beat many " +
        "shallow ones, and an empty list is fine when the PR is good.\n\n" +
        "Outside the block keep replies short (under ~250 words). In follow-ups, answer the developer's questions; if they convince you " +
        "a finding is wrong, or ask you to change the list, send the whole revised block. Never claim you approved, posted or merged " +
        "anything: agentd posts only the findings the developer chooses.";

    public static IReadOnlyList<string> Args(BrainstormTurn turn, ClaudeOptions o)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(o);
        var args = new List<string>
        {
            "-p", turn.Prompt,
            turn.Resume ? "--resume" : "--session-id", turn.Session.ToString(),
            "--output-format", "stream-json", "--verbose",
            "--max-turns", MaxTurns.ToString(CultureInfo.InvariantCulture),
            "--append-system-prompt", turn.Kind switch { ThreadTurnKind.Review => ReviewRules, ThreadTurnKind.FollowUp => FollowUpRules, _ => Rules },
            "--strict-mcp-config",
            "--allowedTools", string.Join(",", o.ReadOnlyTools.Where(t => !t.StartsWith("mcp__", StringComparison.Ordinal))),
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

        return args;
    }

    public async Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var o = options.Value;
        var dir = Path.Combine(Paths.Expand(o.TranscriptRoot), $"{turn.Kind switch { ThreadTurnKind.Review => "review", ThreadTurnKind.FollowUp => "followup", _ => "idea" }}-{turn.IdeaId}");
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
        foreach (var arg in Args(turn, o))
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
