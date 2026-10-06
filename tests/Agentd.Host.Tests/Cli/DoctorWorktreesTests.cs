using Agentd.Host.Cli.Commands;
using Agentd.Infrastructure.Git;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Host.Tests.Cli;

[TestClass]
public sealed class DoctorWorktreesTests
{
    [TestMethod]
    public void Doctor_reports_the_checkouts_their_size_and_the_free_disk()
    {
        var root = Directory.CreateTempSubdirectory("agentd-doctor-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sysmin", "wi-5617", "src"));
            File.WriteAllBytes(Path.Combine(root, "sysmin", "wi-5617", "src", "big.bin"), new byte[3 * 1024 * 1024]);
            Directory.CreateDirectory(Path.Combine(root, "sysmin", "idea-1"));
            var services = new ServiceCollection().Configure<GitOptions>(o => o.WorktreeRoot = root).BuildServiceProvider();

            var result = DoctorCommand.Worktrees(services);

            Assert.AreEqual("Worktrees", result.Check);
            StringAssert.StartsWith(result.Detail, "2 checkout(s), 3 MB;");
            StringAssert.Contains(result.Detail, " free of ");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
