using System.CommandLine;
using System.Globalization;
using System.Text;
using Agentd.Application.Reviews;
using Agentd.Host.Cli.Review;

namespace Agentd.Host.Cli.Commands;

/// <summary>
/// <c>agentd review</c>: your uncommitted (or unpushed, with <c>--base</c>) work, reviewed by your own <c>claude</c> before you
/// push (docs/architect/review-sessions.md §5). The findings are printed and saved to <c>.agentd/review.md</c> for your agent.
/// </summary>
internal static class ReviewCommand
{
    public const string ReviewFile = ".agentd/review.md";

    public static Command Create(CliContext context, RunTool? run = null, Func<string>? currentDirectory = null)
    {
        var baseRef = new Option<string?>("--base") { Description = "Review everything since this branch or commit (your commits plus uncommitted changes), e.g. develop." };
        var untracked = new Option<bool>("--untracked") { Description = "Include new files git doesn't track yet." };
        var model = new Option<string?>("--model") { Description = "Your claude's model for the review (default: its own)." };
        var effort = new Option<string?>("--effort") { Description = "Its effort: low, medium, high, xhigh or max." };
        var noPage = new Option<bool>("--no-page") { Description = "Only print the findings (no review page on localhost)." };
        var noOpen = new Option<bool>("--no-open") { Description = "Print the review page's link instead of opening your browser." };
        var fix = new Option<bool>("--fix") { Description = "After Send, let your claude fix what you kept (edits only, no commits), then review again." };
        var maxRounds = new Option<int>("--rounds") { Description = "With --fix: at most this many review rounds (default 3).", DefaultValueFactory = _ => 3 };
        var command = new Command("review", "Review your local changes with your own claude before you push (read-only); the findings go to .agentd/review.md for your agent.")
        {
            baseRef, untracked, model, effort, noPage, noOpen, fix, maxRounds,
        };
        command.SetAction(async (parse, ct) =>
        {
            var tools = run ?? LocalReview.Run;
            var review = new LocalReview(tools);
            var options = new LocalReviewOptions(parse.GetValue(baseRef), parse.GetValue(untracked), parse.GetValue(model), parse.GetValue(effort));
            var rounds = parse.GetValue(fix) ? Math.Clamp(parse.GetValue(maxRounds), 1, 10) : 1;
            for (var round = 1; round <= rounds; round++)
            {
                await context.Error.WriteLineAsync(round == 1
                    ? "🔍 Reviewing your changes with your claude (read-only)…"
                    : $"🔍 Round {round}: reviewing the fixed changes…").ConfigureAwait(false);
                var outcome = await review.RunAsync((currentDirectory ?? Directory.GetCurrentDirectory)(), options, ct).ConfigureAwait(false);
                if (outcome.Result is not { } result)
                {
                    await context.Error.WriteLineAsync(outcome.Problem).ConfigureAwait(false);
                    return outcome.Problem?.StartsWith("Nothing to review", StringComparison.Ordinal) == true ? ExitCodes.Ok : ExitCodes.Error;
                }

                var markdown = Render(outcome.BaseLabel, outcome.Files.Count, result);
                var path = Path.Combine(outcome.Root, ReviewFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, markdown, ct).ConfigureAwait(false);
                await context.Out.WriteLineAsync(markdown).ConfigureAwait(false);
                if (result.Findings.Count == 0)
                {
                    await context.Error.WriteLineAsync("✅ Nothing that can break the app or slow it down.").ConfigureAwait(false);
                    return ExitCodes.Ok;
                }

                await context.Error.WriteLineAsync($"Saved to {ReviewFile}. Tell your agent: \"fix what's in {ReviewFile}\" (and keep it out of git).").ConfigureAwait(false);
                var feedback = parse.GetValue(noPage) || !LocalReviewServer.Available
                    ? markdown
                    : await PageAsync(context, outcome, result, path, parse.GetValue(noOpen), tools, ct).ConfigureAwait(false);
                if (feedback is null || !parse.GetValue(fix))
                {
                    return ExitCodes.Ok;
                }

                if (!feedback.Contains("Fix these", StringComparison.Ordinal))
                {
                    await context.Error.WriteLineAsync("Nothing kept to fix.").ConfigureAwait(false);
                    return ExitCodes.Ok;
                }

                await context.Error.WriteLineAsync("🔧 Your claude is fixing what you kept (edits only: no commits, no pushes)…").ConfigureAwait(false);
                var fixedIt = await review.FixAsync(outcome.Root, feedback, options, ct).ConfigureAwait(false);
                await context.Error.WriteLineAsync(fixedIt).ConfigureAwait(false);
                if (fixedIt.StartsWith("⚠️", StringComparison.Ordinal))
                {
                    return ExitCodes.Error;
                }
            }

            await context.Error.WriteLineAsync($"Stopped after {rounds} round(s); {ReviewFile} has what's left.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>The review page on localhost until Send (which rewrites the file with your decisions and comments) or Ctrl+C.</summary>
    /// <returns>What was sent (the file's new text), or null when stopped with Ctrl+C.</returns>
    private static async Task<string?> PageAsync(CliContext context, LocalReviewResult outcome, ReviewResult result, string path, bool noOpen, RunTool run, CancellationToken ct)
    {
        var session = new LocalReviewSession(Path.GetFileName(outcome.Root), outcome.BaseLabel, outcome.BaseCommit, outcome.Diff, outcome.Files, result, Environment.UserName);
        await using var server = await LocalReviewServer.StartAsync(session, async text =>
        {
            await File.WriteAllTextAsync(path, text, CancellationToken.None).ConfigureAwait(false);
            return ReviewFile;
        }, ct).ConfigureAwait(false);
        await context.Error.WriteLineAsync($"📝 Review page: {server.Url}\n   Keep, edit or drop findings and comment, then Send (it rewrites {ReviewFile}). Ctrl+C stops.").ConfigureAwait(false);
        if (!noOpen)
        {
            var opener = OperatingSystem.IsMacOS() ? "open" : OperatingSystem.IsWindows() ? "explorer" : "xdg-open";
            _ = run(opener, [server.Url.ToString()], outcome.Root, CancellationToken.None);   // best effort: the link is printed anyway
        }

        try
        {
            var written = await server.Sent.WaitAsync(ct).ConfigureAwait(false);
            var text = await File.ReadAllTextAsync(path, CancellationToken.None).ConfigureAwait(false);
            await context.Out.WriteLineAsync(text).ConfigureAwait(false);
            await context.Error.WriteLineAsync($"✅ Sent to {written}.").ConfigureAwait(false);
            return text;
        }
        catch (OperationCanceledException)
        {
            await context.Error.WriteLineAsync($"Stopped. {ReviewFile} has every finding.").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>The findings as your agent (and you) read them: worst first, each with where, why and the fix.</summary>
    internal static string Render(string baseLabel, int files, ReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var sb = new StringBuilder();
        sb.AppendLine("# Review findings (agentd review)").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Compared with {baseLabel}; {files} file(s). {result.Summary}").AppendLine();
        if (result.Findings.Count == 0)
        {
            return sb.AppendLine("No findings: nothing that can break the app or slow it down.").ToString();
        }

        sb.AppendLine("Fix these, then run `agentd review` again:").AppendLine();
        var n = 0;
        foreach (var f in result.Findings)
        {
            var where = f.File is null ? string.Empty : $" · `{f.File}{(f.Line is { } l ? string.Create(CultureInfo.InvariantCulture, $":{l}") : string.Empty)}`";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{++n}. {(f.Severity == ReviewFindings.Breaks ? "🔴" : "🟠")} **{f.Title}**{where}");
            if (!string.IsNullOrWhiteSpace(f.Detail))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"   {f.Detail.Trim().ReplaceLineEndings(" ")}");
            }

            if (!string.IsNullOrWhiteSpace(f.Suggestion))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"   Fix: {f.Suggestion.Trim().ReplaceLineEndings(" ")}");
            }
        }

        return sb.ToString();
    }
}
