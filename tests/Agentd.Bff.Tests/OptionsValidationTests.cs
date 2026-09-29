using Microsoft.Extensions.Options;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class OptionsValidationTests
{
    [TestMethod]
    public void Invalid_configuration_fails_fast_at_startup()
    {
        using var factory = new AgentdHostFactory(new Dictionary<string, string?> { ["Agentd:Web:Urls"] = "not a url" });

        var ex = Assert.ThrowsExactly<OptionsValidationException>(() => factory.CreateClient());

        StringAssert.Contains(ex.Message, "Urls");
    }
}
