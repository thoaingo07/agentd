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
        var command = new Command("review", "Review your local changes with your own claude before you push (read-only); the findings go to .agentd/review.md for your agent.")
        {
            baseRef, untracked, model, effort, noPage, noOpen,
        };
        command.SetAction(async (parse, ct) =>
        {
            await context.Error.WriteLineAsync("🔍 Reviewing your changes with your claude (read-only)…").ConfigureAwait(false);
            var review = new LocalReview(run ?? LocalReview.Run);
            var outcome = await review.RunAsync((currentDirectory ?? Directory.GetCurrentDirectory)(),
                new LocalReviewOptions(parse.GetValue(baseRef), parse.GetValue(untracked), parse.GetValue(model), parse.GetValue(effort)), ct).ConfigureAwait(false);
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
            await context.Error.WriteLineAsync(result.Findings.Count == 0
                ? "✅ Nothing that can break the app or slow it down."
                : $"Saved to {ReviewFile}. Tell your agent: \"fix what's in {ReviewFile}\" (and keep it out of git).").ConfigureAwait(false);
            if (result.Findings.Count == 0 || parse.GetValue(noPage) || !LocalReviewServer.Available)
            {
                return ExitCodes.Ok;
            }

            return await PageAsync(context, outcome, result, path, parse.GetValue(noOpen), run ?? LocalReview.Run, ct).ConfigureAwait(false);
        });
        return command;
    }

    /// <summary>The review page on localhost until Send (which rewrites the file with your decisions and comments) or Ctrl+C.</summary>
    private static async Task<int> PageAsync(CliContext context, LocalReviewResult outcome, ReviewResult result, string path, bool noOpen, RunTool run, CancellationToken ct)
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
            await context.Out.WriteLineAsync(await File.ReadAllTextAsync(path, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            await context.Error.WriteLineAsync($"✅ Sent to {written}. Tell your agent: \"fix what's in {written}\".").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await context.Error.WriteLineAsync($"Stopped. {ReviewFile} has every finding.").ConfigureAwait(false);
        }

        return ExitCodes.Ok;
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
