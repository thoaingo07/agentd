using Agentd.Web.Vite;

namespace Agentd.Web.Tests;

[TestClass]
public sealed class EmbeddedWebAssetsTests
{
    private readonly EmbeddedWebAssets _assets = new(typeof(EmbeddedWebAssetsTests).Assembly);

    [TestMethod]
    public async Task An_embedded_asset_is_served_under_the_content_prefix()
    {
        var file = _assets.GetFileInfo("/_content/Agentd.Web/assets/app.js");

        Assert.IsTrue(_assets.HasAssets);
        Assert.IsTrue(file.Exists);
        Assert.AreEqual(("app.js", false), (file.Name, file.IsDirectory));
        Assert.IsNull(file.PhysicalPath, "never a path on disk");
        using var reader = new StreamReader(file.CreateReadStream());
        var text = await reader.ReadToEndAsync();
        Assert.AreEqual("console.log(\"embedded\")\n", text);
        Assert.AreEqual(text.Length, file.Length);
    }

    [TestMethod]
    [DataRow("/assets/app.js")]
    [DataRow("/_content/Agentd.Web/assets/missing.js")]
    [DataRow("/_content/Agentd.Web/../wwwroot/assets/app.js")]
    [DataRow("/_content/Other/assets/app.js")]
    [DataRow("")]
    public void Anything_else_is_not_found(string path)
    {
        Assert.IsFalse(_assets.GetFileInfo(path).Exists);
    }

    [TestMethod]
    public void An_assembly_built_without_the_web_has_no_assets()
    {
        Assert.IsFalse(new EmbeddedWebAssets(typeof(string).Assembly).HasAssets);
    }
}
