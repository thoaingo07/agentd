using Agentd.Application.Reviews;
using Agentd.Domain.Repositories;
using static Agentd.Application.Tests.Reviews.ReviewSessionServiceTests;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Reviews;

[TestClass]
public sealed class ReviewSearchTests
{
    private readonly TestContext _t = new();
    private readonly MemoryReviewSessions _store = new();
    private readonly Branch _branch = new();

    [TestMethod]
    public async Task File_names_match_every_word_ignoring_case_with_name_matches_first()
    {
        var id = await PinnedAsync();

        var sync = (await Search().FilesAsync(id, "sync", 10, default)).Value!;
        var both = (await Search().FilesAsync(id, "SRC test", 10, default)).Value!;

        CollectionAssert.AreEqual(new[] { "src/Sync.cs", "tests/SyncTests.cs", "docs/sync/how.md" }, sync.ToArray(), "the name first, then shorter paths");
        CollectionAssert.AreEqual(new[] { "src/test/Helpers.cs" }, both.ToArray());
        Assert.AreEqual("h1", _branch.Commits.Distinct().Single(), "at the session's head commit");
    }

    [TestMethod]
    public async Task Text_goes_to_git_at_the_head_commit_and_a_file_is_read_there()
    {
        var id = await PinnedAsync();

        var hits = (await Search().TextAsync(id, "ReadAll(", ignoreCase: true, 999, default)).Value!;
        var file = (await Search().FileAsync(id, "src/Sync.cs", default)).Value!;

        Assert.AreEqual(("src/Sync.cs", 2), (hits.Single().Path, hits.Single().Line));
        Assert.AreEqual((ReviewSearch.MaxResults, true), (_branch.Limit, _branch.IgnoreCase), "the limit is capped");
        Assert.AreEqual("class Sync {}", file.Text);

        await Search().FileAsync(id, "src/Sync.cs", default, atBase: true);
        Assert.AreEqual("m1", _branch.Commits[^1], "a deleted file is read at the base commit");
    }

    [TestMethod]
    public async Task Bad_queries_and_paths_are_refused_and_an_unpinned_review_has_nothing_to_search()
    {
        var id = await PinnedAsync();
        var unpinned = await _store.InsertAsync("sysmin", ReviewTargets.Branch, null, "feature/x", "develop", "dev@example.com", null, null, default);

        Assert.AreEqual("validation", (await Search().FilesAsync(id, "  ", 10, default)).Error?.Code);
        Assert.AreEqual("validation", (await Search().TextAsync(id, "a\nb", false, 10, default)).Error?.Code);
        foreach (var path in new[] { "../etc/passwd", "/etc/passwd", "-rf", "src/../x", "" })
        {
            Assert.AreEqual("validation", (await Search().FileAsync(id, path, default)).Error?.Code, path);
        }

        Assert.AreEqual("not_found", (await Search().FileAsync(id, "src/missing.cs", default)).Error?.Code);
        Assert.AreEqual("conflict", (await Search().FilesAsync(unpinned, "sync", 10, default)).Error?.Code);
        Assert.AreEqual("not_found", (await Search().FilesAsync(999, "sync", 10, default)).Error?.Code);
    }

    private async Task<long> PinnedAsync()
    {
        var id = await _store.InsertAsync("sysmin", ReviewTargets.Branch, null, "feature/x", "develop", "dev@example.com", null, null, default);
        await _store.PinAsync(id, "m1", "h1", null, default);
        return id;
    }

    private ReviewSearch Search() => new(_store, _t.Registry, _branch);

    private sealed class Branch : IBranchSearch
    {
        public List<string> Commits { get; } = [];

        public int Limit { get; private set; }

        public bool IgnoreCase { get; private set; }

        public Task<IReadOnlyList<string>> ListFilesAsync(Repository repository, string commit, CancellationToken cancellationToken)
        {
            Commits.Add(commit);
            return Task.FromResult<IReadOnlyList<string>>(["docs/sync/how.md", "tests/SyncTests.cs", "src/Sync.cs", "src/test/Helpers.cs", "README.md"]);
        }

        public Task<IReadOnlyList<TextHit>> GrepAsync(Repository repository, string commit, string text, bool ignoreCase, int limit, CancellationToken cancellationToken)
        {
            (Limit, IgnoreCase) = (limit, ignoreCase);
            Commits.Add(commit);
            return Task.FromResult<IReadOnlyList<TextHit>>([new TextHit("src/Sync.cs", 2, "var rows = ReadAll(t);", 11, 8)]);
        }

        public Task<CommitFile?> ReadFileAsync(Repository repository, string commit, string path, int maxBytes, CancellationToken cancellationToken)
        {
            Commits.Add(commit);
            return Task.FromResult(path == "src/Sync.cs" ? new CommitFile(path, "class Sync {}", false, false) : null);
        }
    }
}
