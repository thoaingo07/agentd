using Agentd.Domain.Repositories;

namespace Agentd.Domain.Tests.Repositories;

[TestClass]
public sealed class RemoteUrlTests
{
    [TestMethod]
    [DataRow("git@ssh.dev.azure.com:v3/ermsystem/Portal/sysmin")]
    [DataRow("ermsystem@vs-ssh.visualstudio.com:v3/ermsystem/Portal/sysmin")]
    [DataRow("git@erm-azdo:v3/ermsystem/Portal/sysmin")]
    [DataRow("ssh://git@ssh.dev.azure.com:22/v3/ermsystem/Portal/sysmin")]
    [DataRow("https://dev.azure.com/ermsystem/Portal/_git/sysmin")]
    [DataRow("https://ermsystem@dev.azure.com/ermsystem/Portal/_git/sysmin")]
    [DataRow("https://ermsystem.visualstudio.com/Portal/_git/sysmin")]
    [DataRow("https://ermsystem.visualstudio.com/DefaultCollection/Portal/_git/sysmin/")]
    public void Parses_every_supported_azure_devops_form(string url)
    {
        var parsed = RemoteUrl.Parse(url);

        Assert.IsTrue(parsed.IsSuccess, parsed.Error?.ToString());
        Assert.AreEqual(new AzureDevOpsRepo("ermsystem", "Portal", "sysmin"), parsed.Value!.AzureDevOps);
    }

    [TestMethod]
    public void Decodes_escaped_project_names() =>
        Assert.AreEqual("My Project", RemoteUrl.Parse("https://dev.azure.com/org/My%20Project/_git/repo").Value!.AzureDevOps.Project);

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("https://github.com/thoaingo07/agentd")]
    [DataRow("not a url")]
    public void Rejects_unsupported_urls(string url) =>
        Assert.AreEqual("validation", RemoteUrl.Parse(url).Error?.Code);
}
