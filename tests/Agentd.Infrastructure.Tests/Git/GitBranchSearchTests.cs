using Agentd.Infrastructure.Git;

namespace Agentd.Infrastructure.Tests.Git;

[TestClass]
public sealed class GitBranchSearchTests
{
    [TestMethod]
    public async Task Lists_every_file_at_the_commit_and_greps_literally_with_line_numbers()
    {
        using var box = await SeededAsync();
        var (search, commit) = (new GitBranchSearch(box.Git, box.Manager), await HeadAsync(box));

        var files = await search.ListFilesAsync(box.Repository, commit, default);
        var hits = await search.GrepAsync(box.Repository, commit, "ReadAll(", ignoreCase: false, 50, default);
        var anyCase = await search.GrepAsync(box.Repository, commit, "readall(", ignoreCase: true, 50, default);
        var none = await search.GrepAsync(box.Repository, commit, "nothing like this", ignoreCase: false, 50, default);
        var option = await search.GrepAsync(box.Repository, commit, "--version", ignoreCase: false, 50, default);

        CollectionAssert.AreEquivalent(new[] { "README.md", "src/Sync.cs", "docs/a b.md", "logo.bin", "-rf.txt" }, files.ToArray());
        Assert.HasCount(2, hits, "the literal text, not a regex; the binary file is skipped");
        Assert.AreEqual(("docs/a b.md", 1), (hits[0].Path, hits[0].Line), "a path with a space; files in path order");
        Assert.AreEqual(("src/Sync.cs", 2), (hits[1].Path, hits[1].Line));
        Assert.AreEqual("ReadAll(", hits[1].Snippet.Substring(hits[1].MatchStart, hits[1].MatchLength));
        Assert.HasCount(2, anyCase);
        Assert.IsEmpty(none);
        Assert.AreEqual("-rf.txt", option.Single().Path, "text that looks like an option is searched for, not run");
    }

    [TestMethod]
    public async Task Reads_a_file_at_the_commit_and_knows_binary_missing_and_folders()
    {
        using var box = await SeededAsync();
        var (search, commit) = (new GitBranchSearch(box.Git, box.Manager), await HeadAsync(box));

        var file = await search.ReadFileAsync(box.Repository, commit, "src/Sync.cs", 1000, default);
        var cut = await search.ReadFileAsync(box.Repository, commit, "src/Sync.cs", 10, default);
        var binary = await search.ReadFileAsync(box.Repository, commit, "logo.bin", 1000, default);

        StringAssert.StartsWith(file!.Text, "class Sync {");
        Assert.IsFalse(file.Truncated);
        Assert.AreEqual((10, true), (cut!.Text.Length, cut.Truncated));
        Assert.IsTrue(binary!.Binary);
        Assert.IsNull(await search.ReadFileAsync(box.Repository, commit, "src/missing.cs", 1000, default));
        Assert.IsNull(await search.ReadFileAsync(box.Repository, commit, "src", 1000, default), "a folder isn't a file");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => search.ReadFileAsync(box.Repository, "--output=x", "src/Sync.cs", 1000, default));
    }

    [TestMethod]
    public void A_long_line_is_cut_around_the_match()
    {
        var hit = GitBranchSearch.Hit("app.min.js", 1, new string('a', 500) + "needle" + new string('b', 500), "needle", ignoreCase: false);

        Assert.AreEqual("needle", hit.Snippet.Substring(hit.MatchStart, hit.MatchLength));
        Assert.IsTrue(hit.Snippet.StartsWith('…') && hit.Snippet.EndsWith('…'));
        Assert.IsLessThan(260, hit.Snippet.Length);
    }

    private static async Task<GitSandbox> SeededAsync()
    {
        var box = new GitSandbox();
        var seed = Path.Combine(box.Root, "seed");
        Directory.CreateDirectory(Path.Combine(seed, "src"));
        Directory.CreateDirectory(Path.Combine(seed, "docs"));
        await File.WriteAllTextAsync(Path.Combine(seed, "src", "Sync.cs"), "class Sync {\n  var rows = ReadAll(table);\n  // ReadAll.( is not a regex\n}\n");
        await File.WriteAllTextAsync(Path.Combine(seed, "docs", "a b.md"), "Calls ReadAll( once.\n");
        await File.WriteAllBytesAsync(Path.Combine(seed, "logo.bin"), [0, 1, 2, .. "ReadAll("u8.ToArray(), 0]);
        await File.WriteAllTextAsync(Path.Combine(seed, "-rf.txt"), "git --version\n");
        GitSandbox.Run(seed, "add", "--", ".");
        GitSandbox.Run(seed, "-c", "user.name=seed", "-c", "user.email=seed@x", "commit", "-m", "files");
        GitSandbox.Run(seed, "push", "origin", "HEAD:develop");
        return box;
    }

    private static async Task<string> HeadAsync(GitSandbox box)
    {
        await box.Manager.EnsureCloneAsync(box.Repository, default);
        return (await box.Manager.ResolveCommitAsync(box.Repository, "develop", default))!;
    }
}
