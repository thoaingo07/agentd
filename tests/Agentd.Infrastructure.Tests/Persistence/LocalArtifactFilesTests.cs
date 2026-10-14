using Agentd.Application.Artifacts;
using Agentd.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
public sealed class LocalArtifactFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"agentd-artifacts-{Guid.NewGuid():N}", "pages");

    [TestMethod]
    public async Task A_page_is_a_raw_html_file_in_the_folder_read_back_replaced_and_deleted()
    {
        var files = Files(_folder);

        await files.PutAsync("sync-report-0a1b2c3d", "<!doctype html><p>v1</p>", default);
        await files.PutAsync("sync-report-0a1b2c3d", "<!doctype html><p>v2</p>", default);

        var path = Path.Combine(_folder, "sync-report-0a1b2c3d.html");
        Assert.AreEqual("<!doctype html><p>v2</p>", await File.ReadAllTextAsync(path), "raw HTML, the folder made as needed");
        Assert.AreEqual("<!doctype html><p>v2</p>", await files.GetAsync("sync-report-0a1b2c3d", default));
        Assert.IsEmpty(Directory.GetFiles(_folder, "*.tmp"), "written through a temporary file");

        await files.DeleteAsync("sync-report-0a1b2c3d", default);
        await files.DeleteAsync("sync-report-0a1b2c3d", default);   // already gone: fine
        Assert.IsNull(await files.GetAsync("sync-report-0a1b2c3d", default));
        Assert.IsNull(await Files(Path.Combine(_folder, "missing")).GetAsync("other-1", default), "no folder yet: no page");
    }

    [TestMethod]
    [DataRow("../secrets")]
    [DataRow("a/b")]
    [DataRow("..")]
    [DataRow("Page-1")]
    [DataRow("page.html")]
    [DataRow("")]
    public async Task Only_a_pages_own_name_is_accepted_so_nothing_escapes_the_folder(string name)
    {
        var files = Files(_folder);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => files.PutAsync(name, "<p>x</p>", default));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => files.GetAsync(name, default));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => files.DeleteAsync(name, default));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(_folder)), "nothing written anywhere");
    }

    [TestMethod]
    public async Task A_folder_under_the_home_is_expanded_and_no_folder_is_refused()
    {
        var relative = Path.GetRelativePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _folder);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            Assert.Inconclusive("The temp folder isn't under the home here.");
        }

        await Files("~/" + relative.Replace('\\', '/')).PutAsync("home-page-1", "<p>home</p>", default);

        Assert.IsTrue(File.Exists(Path.Combine(_folder, "home-page-1.html")));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Files(null).GetAsync("a-1", default));
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_folder)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static LocalArtifactFiles Files(string? folder) => new(Options.Create(new ArtifactOptions { Folder = folder }));
}
