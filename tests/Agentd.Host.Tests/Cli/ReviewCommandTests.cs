using System.CommandLine;
using System.Diagnostics;
using Agentd.Host.Cli;
using Agentd.Host.Cli.Commands;
using Agentd.Host.Cli.Review;

namespace Agentd.Host.Tests.Cli;

[TestClass]
[OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
public sealed class ReviewCommandTests : IDisposable
{
    private const string Findings = """
        {"type":"result","is_error":false,"result":"One real bug.\n\n```review-findings\n{\"summary\":\"Pages the sync by key. One real bug.\",\"findings\":[{\"severity\":\"breaks\",\"file\":\"src/Sync.cs\",\"line\":2,\"title\":\"Unbounded read\",\"detail\":\"Large tables load at once.\",\"suggestion\":\"Page by the primary key.\"}]}\n```"}
        """;

    private readonly string _repo = Directory.CreateTempSubdirectory("agentd-review-").FullName;
    private readonly List<(IReadOnlyList<string> Args, string Cwd)> _claude = [];
    private string _reply = Findings;
    private string? _secondReview;
    private int _claudeExit;
    private int _fixExit;
    private Action? _onFix;

    public ReviewCommandTests()
    {
        Git("init", "--initial-branch=develop");
        Write("src/Sync.cs", "class Sync {}\n");
        Git("add", ".");
        Git("commit", "-m", "initial");
    }

    [TestMethod]
    public async Task Uncommitted_changes_are_reviewed_by_your_claude_and_saved_for_your_agent()
    {
        Write("src/Sync.cs", "class Sync {\n  var rows = ReadAll();\n}\n");

        var (code, output, error) = await RunAsync([]);

        Assert.AreEqual(ExitCodes.Ok, code, error);
        var (args, cwd) = _claude.Single();
        Assert.AreEqual(_repo, cwd, "in your repository");
        StringAssert.Contains(args[1], "+  var rows = ReadAll();", "the diff is in the prompt");
        StringAssert.Contains(args[1], "This isn't a pull request");
        Assert.AreEqual("plan", args[args.ToList().IndexOf("--permission-mode") + 1], "read-only");
        CollectionAssert.Contains(args.ToList(), Infrastructure.Claude.ClaudeBrainstormAgent.ReviewRules);
        var saved = await File.ReadAllTextAsync(Path.Combine(_repo, ".agentd", "review.md"));
        StringAssert.Contains(saved, "1. 🔴 **Unbounded read** · `src/Sync.cs:2`");
        StringAssert.Contains(saved, "Fix: Page by the primary key.");
        StringAssert.Contains(output, "Pages the sync by key. One real bug.");
        StringAssert.Contains(error, "fix what's in .agentd/review.md");
    }

    [TestMethod]
    public async Task With_a_base_it_reviews_your_commits_too_and_untracked_files_on_request()
    {
        Git("checkout", "-b", "feature/keyset");
        Write("src/Sync.cs", "class Sync { int page; }\n");
        Git("commit", "-am", "page");
        Write("src/New.cs", "class New {}\n");

        var (code, _, error) = await RunAsync(["--base", "develop", "--untracked", "--model", "opus", "--effort", "high"]);

        Assert.AreEqual(ExitCodes.Ok, code, error);
        var args = _claude.Single().Args.ToList();
        StringAssert.Contains(args[1], "+class Sync { int page; }", "the commit since develop");
        StringAssert.Contains(args[1], "+class New {}", "the untracked file");
        Assert.AreEqual(("opus", "high"), (args[args.IndexOf("--model") + 1], args[args.IndexOf("--effort") + 1]));
    }

    [TestMethod]
    public async Task No_changes_means_nothing_to_review_and_no_claude()
    {
        var (code, _, error) = await RunAsync([]);

        Assert.AreEqual(ExitCodes.Ok, code);
        StringAssert.Contains(error, "Nothing to review");
        Assert.IsEmpty(_claude);
    }

    [TestMethod]
    [DataRow(127, "", "isn't installed")]
    [DataRow(1, """{"is_error":true,"result":"Invalid API key"}""", "didn't finish the review: Invalid API key")]
    [DataRow(0, """{"result":"looks fine, no block"}""", "couldn't be read")]
    public async Task Claude_problems_are_explained(int exit, string reply, string expected)
    {
        Write("src/Sync.cs", "class Sync { }\n");
        (_claudeExit, _reply) = (exit, reply);

        var (code, _, error) = await RunAsync([]);

        Assert.AreEqual(ExitCodes.Error, code);
        StringAssert.Contains(error, expected);
        Assert.IsFalse(File.Exists(Path.Combine(_repo, ".agentd", "review.md")));
    }

    [TestMethod]
    public async Task Fix_lets_your_claude_fix_the_findings_then_reviews_again_until_clean()
    {
        Write("src/Sync.cs", "class Sync {\n  var rows = ReadAll();\n}\n");
        _onFix = () => Write("src/Sync.cs", "class Sync {\n  var rows = ReadPage(5000);\n}\n");
        _secondReview = """{"result":"Clean now.\n\n```review-findings\n{\"summary\":\"Pages by key now.\",\"findings\":[]}\n```"}""";

        var (code, _, error) = await RunAsync(["--fix"]);

        Assert.AreEqual(ExitCodes.Ok, code, error);
        CollectionAssert.AreEqual(new[] { "plan", "acceptEdits", "plan" }, _claude.Select(c => c.Args[c.Args.ToList().IndexOf("--permission-mode") + 1]).ToList(), "review, fix, review");
        StringAssert.Contains(_claude[1].Args[1], "Fix these review findings");
        StringAssert.Contains(_claude[1].Args[1], "don't run `agentd review`");
        StringAssert.Contains(_claude[1].Args[1], "🔴 **Unbounded read**", "the findings go to the fix");
        StringAssert.Contains(_claude[2].Args[1], "+  var rows = ReadPage(5000);", "round 2 reviews the fixed code");
        StringAssert.Contains(error, "🔧 Fixed the loop.");
        StringAssert.Contains(error, "Round 2");
        StringAssert.Contains(error, "✅ Nothing that can break the app");
    }

    [TestMethod]
    public async Task Fix_stops_after_the_rounds_and_reports_a_failed_fix()
    {
        Write("src/Sync.cs", "class Sync { }\n");

        var (rounds, _, stopped) = await RunAsync(["--fix", "--rounds", "1"]);
        _fixExit = 1;
        _claude.Clear();
        var (failed, _, why) = await RunAsync(["--fix"]);

        Assert.AreEqual((ExitCodes.Ok, ExitCodes.Error), (rounds, failed));
        StringAssert.Contains(stopped, "Stopped after 1 round(s)");
        StringAssert.Contains(why, "⚠️ Your claude couldn't fix it");
    }

    [TestMethod]
    public void A_big_change_is_read_with_git_instead_of_pasted() =>
        StringAssert.Contains(LocalReview.Prompt("abc1234", "develop", new string('x', LocalReview.InlineDiffBytes + 1), false), "too big to paste: read it with `git diff abc1234`");

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private async Task<(int Code, string Output, string Error)> RunAsync(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        await using var context = new CliContext(output, error, () => throw new InvalidOperationException("agentd review needs no services"));
        var command = ReviewCommand.Create(context, Tools, () => Path.Combine(_repo, "src"));
        var root = new RootCommand { command };
        var code = await root.Parse(["review", "--no-page", .. args]).InvokeAsync();
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>Real git; claude is faked (records how it was called, answers with <see cref="_reply"/>).</summary>
    private Task<ToolResult> Tools(string file, IReadOnlyList<string> args, string cwd, CancellationToken ct)
    {
        if (file == "git")
        {
            return LocalReview.Run(file, args, cwd, ct);
        }

        _claude.Add((args, cwd));
        if (args.Contains("acceptEdits"))
        {
            _onFix?.Invoke();
            return Task.FromResult(_fixExit == 0 ? new ToolResult(0, """{"result":"Fixed the loop."}""", string.Empty) : new ToolResult(1, string.Empty, "fix failed"));
        }

        var reply = _claude.Count(c => !c.Args.Contains("acceptEdits")) > 1 && _secondReview is not null ? _secondReview : _reply;
        return Task.FromResult(new ToolResult(_claudeExit, reply, _claudeExit == 0 ? string.Empty : "claude failed"));
    }

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_repo, path))!);
        File.WriteAllText(Path.Combine(_repo, path), content);
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "user.name=dev", "-c", "user.email=dev@x" }.Concat(args))
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.AreEqual(0, p.ExitCode, p.StandardError.ReadToEnd());
    }
}
