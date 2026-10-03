using Agentd.Infrastructure.Messaging.Discord;

namespace Agentd.Infrastructure.Tests.Messaging.Discord;

[TestClass]
public sealed class DiscordRendererTests
{
    [TestMethod]
    [DataRow("hey @everyone and @here", "hey @​everyone and @​here")]
    [DataRow("ping <@123> <@!45> <@&6> <#789>", "ping <​@123> <​@!45> <​@&6> <​#789>")]
    [DataRow("[click](javascript:alert(1))", "\\[click\\] (javascript:alert(1))")]
    [DataRow("[docs](https://example.com/x)", "[docs](https://example.com/x)")]
    [DataRow("# not a heading", "\\# not a heading")]
    [DataRow("-# not subtext", "\\-# not subtext")]
    [DataRow("**bold** *italic* `code`", "**bold** *italic* `code`")]
    public void Text_is_inert_but_formatting_survives(string input, string expected) =>
        Assert.AreEqual(expected, DiscordRenderer.RenderMarkdown(input));

    [TestMethod]
    public void Code_blocks_are_left_exactly_as_written()
    {
        const string Code = "```ts\nconst who = '@everyone <@1>';\n# comment\n```";

        Assert.AreEqual($"Before @​everyone\n{Code}\nafter", DiscordRenderer.RenderMarkdown($"Before @everyone\n{Code}\nafter"));
    }
}
