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

    [TestMethod]
    public void A_default_provider_that_is_not_enabled_fails_fast()
    {
        using var factory = new AgentdHostFactory(new Dictionary<string, string?> { ["Agentd:Messaging:DefaultProviders:0"] = "discord" });

        var ex = Assert.ThrowsExactly<OptionsValidationException>(() => factory.CreateClient());

        StringAssert.Contains(ex.Message, "'discord' is not an enabled provider");
    }

    [TestMethod]
    public void An_identity_mapped_to_two_users_fails_fast()
    {
        using var factory = new AgentdHostFactory(new Dictionary<string, string?>
        {
            ["Agentd:Users:0:Name"] = "alice",
            ["Agentd:Users:0:Identities:Discord"] = "42",
            ["Agentd:Users:1:Name"] = "bob",
            ["Agentd:Users:1:Identities:Discord"] = "42",
        });

        var ex = Assert.ThrowsExactly<OptionsValidationException>(() => factory.CreateClient());

        StringAssert.Contains(ex.Message, "mapped to both 'alice' and 'bob'");
    }
}
