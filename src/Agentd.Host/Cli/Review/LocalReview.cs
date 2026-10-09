using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Agentd.Application.Reviews;
using Agentd.Infrastructure.Claude;

namespace Agentd.Host.Cli.Review;

/// <summary>A program's exit code and its two streams, kept apart (a diff or Claude's JSON on stdout, noise on stderr).</summary>
internal sealed record ToolResult(int ExitCode, string Output, string Error);

/// <summary>Runs a program in a folder (tests replace it).</summary>
internal delegate Task<ToolResult> RunTool(string file, IReadOnlyList<string> args, string workingDirectory, CancellationToken ct);

/// <summary>What to review and how (<c>agentd review</c> options).</summary>
internal sealed record LocalReviewOptions(string? Base = null, bool Untracked = false, string? Model = null, string? Effort = null);

/// <summary>The outcome: the repository, what it was compared with, the diff's files, and the reviewer's result (or why there's none).</summary>
internal sealed record LocalReviewResult(string Root, string BaseLabel, IReadOnlyList<string> Files, ReviewResult? Result, string? Problem);

/// <summary>
/// <c>agentd review</c> (docs/architect/review-sessions.md §5): the uncommitted (or unpushed) change in the current repository,
/// reviewed by the developer's own <c>claude</c>, read-only, with the same rules as the server's reviewer. Nothing leaves
/// the machine except what their Claude Code sends to Anthropic.
/// </summary>
internal sealed class LocalReview(RunTool run, string claude = "claude")
{
    /// <summary>Diffs up to this size go in the prompt; bigger ones are read by Claude with git itself.</summary>
    public const int InlineDiffBytes = 60_000;

    public async Task<LocalReviewResult> RunAsync(string directory, LocalReviewOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var top = await run("git", ["rev-parse", "--show-toplevel"], directory, ct).ConfigureAwait(false);
        if (top.ExitCode != 0)
        {
            return new(directory, "", [], null, "This isn't a git repository: run `agentd review` inside one.");
        }

        var root = top.Output.Trim();
        var baseCommit = "HEAD";
        var label = "HEAD (your uncommitted changes)";
        if (!string.IsNullOrWhiteSpace(options.Base))
        {
            var mergeBase = await run("git", ["merge-base", "--end-of-options", options.Base.Trim(), "HEAD"], root, ct).ConfigureAwait(false);
            if (mergeBase.ExitCode != 0)
            {
                return new(root, options.Base, [], null, $"`{options.Base}` isn't a branch or commit here (or shares no history with HEAD).");
            }

            baseCommit = mergeBase.Output.Trim();
            label = $"{options.Base} (where your branch left it, {baseCommit[..Math.Min(7, baseCommit.Length)]}), including uncommitted changes";
        }

        var (diff, files) = await DiffAsync(root, baseCommit, options.Untracked, ct).ConfigureAwait(false);
        if (diff.Length == 0)
        {
            return new(root, label, [], null, $"Nothing to review: no changes against {label}.");
        }

        List<string> args = ["-p", Prompt(baseCommit, label, diff, options.Untracked), "--output-format", "json", "--permission-mode", "plan",
            "--append-system-prompt", ClaudeBrainstormAgent.ReviewRules,
            "--allowedTools", "Read,Grep,Glob,Bash(git diff:*),Bash(git log:*),Bash(git show:*),Bash(git blame:*),Bash(git status:*)"];
        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            args.AddRange(["--model", options.Model.Trim()]);
        }

        if (!string.IsNullOrWhiteSpace(options.Effort))
        {
            args.AddRange(["--effort", options.Effort.Trim()]);
        }

        var reply = await run(claude, args, root, ct).ConfigureAwait(false);
        if (reply.ExitCode == 127)
        {
            return new(root, label, files, null, "Claude Code isn't installed: `npm install -g @anthropic-ai/claude-code`, then `claude` once to sign in.");
        }

        var (text, isError) = Result(reply.Output);
        if (reply.ExitCode != 0 || isError || text is null)
        {
            var why = (string.IsNullOrWhiteSpace(text) ? reply.Error : text).Trim().ReplaceLineEndings(" ");
            return new(root, label, files, null, $"Your claude didn't finish the review: {why[..Math.Min(300, why.Length)]}");
        }

        var (_, result, problem) = ReviewFindings.Extract(text);
        return result is null
            ? new(root, label, files, null, $"Your claude's findings couldn't be read: {problem ?? "no review-findings block"}.")
            : new(root, label, files, result, null);
    }

    /// <summary>The change against <paramref name="baseCommit"/> (staged and unstaged), plus untracked files when asked.</summary>
    private async Task<(string Diff, IReadOnlyList<string> Files)> DiffAsync(string root, string baseCommit, bool untracked, CancellationToken ct)
    {
        var diff = new StringBuilder((await run("git", ["diff", "--no-color", "--no-ext-diff", baseCommit], root, ct).ConfigureAwait(false)).Output);
        var files = (await run("git", ["diff", "--name-only", baseCommit], root, ct).ConfigureAwait(false)).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (untracked)
        {
            var others = (await run("git", ["ls-files", "--others", "--exclude-standard"], root, ct).ConfigureAwait(false)).Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var file in others)
            {
                // Exit code 1 means "there are differences" for --no-index; the diff is on stdout either way.
                diff.Append((await run("git", ["diff", "--no-color", "--no-index", "--", "/dev/null", file], root, ct).ConfigureAwait(false)).Output);
                files.Add(file);
            }
        }

        return (diff.ToString().Trim(), files);
    }

    internal static string Prompt(string baseCommit, string label, string diff, bool untracked)
    {
        var range = baseCommit == "HEAD" ? "`git diff HEAD`" : $"`git diff {baseCommit}`";
        var body = Encoding.UTF8.GetByteCount(diff) <= InlineDiffBytes
            ? $"The change:\n\n```diff\n{diff}\n```"
            : $"The change is too big to paste: read it with {range}{(untracked ? " and `git status`" : string.Empty)}.";
        return string.Create(CultureInfo.InvariantCulture,
            $"This isn't a pull request: it's a developer's local work, reviewed with `agentd review` before they push. Your working directory is their repository " +
            $"(read-only for you). Review exactly the change against {label}: {range}{(untracked ? " plus the new untracked files" : string.Empty)}. ") +
            "There are no PR threads or work items; judge it by the code. End with the review-findings block as usual.\n\n" + body;
    }

    /// <summary>The reply text from <c>claude -p --output-format json</c> (<c>{ "result": …, "is_error": … }</c>); null when it isn't JSON.</summary>
    private static (string? Text, bool IsError) Result(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return (node?["result"]?.GetValue<string>(), node?["is_error"]?.GetValue<bool>() == true);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, false);
        }
    }

    /// <summary>The real runner: stdout and stderr apart, 127 when the program isn't installed.</summary>
    public static async Task<ToolResult> Run(string file, IReadOnlyList<string> args, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = workingDirectory };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new ToolResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ToolResult(127, string.Empty, ex.Message);
        }
    }
}
