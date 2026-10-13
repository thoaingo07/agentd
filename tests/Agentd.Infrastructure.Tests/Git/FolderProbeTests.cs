using Agentd.Infrastructure.Git;

namespace Agentd.Infrastructure.Tests.Git;

[TestClass]
public sealed class FolderProbeTests
{
    [TestMethod]
    public void A_new_folder_is_created_and_left_empty()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentd-probe-{Guid.NewGuid():N}");
        try
        {
            var check = new FolderProbe().Test(Path.Combine(root, "worktrees"));

            Assert.IsTrue(check.Ok, check.Message);
            StringAssert.Contains(check.Message, "is writable");
            Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(root, "worktrees")), "the probe file is removed");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void A_file_in_the_way_fails_with_a_fix()
    {
        var file = Path.GetTempFileName();
        try
        {
            var check = new FolderProbe().Test(file);

            Assert.IsFalse(check.Ok);
            StringAssert.Contains(check.Message, "can't be used");
            Assert.IsNotNull(check.Fix);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
