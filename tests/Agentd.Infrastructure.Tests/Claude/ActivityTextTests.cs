using Agentd.Infrastructure.Claude;

namespace Agentd.Infrastructure.Tests.Claude;

[TestClass]
public sealed class ActivityTextTests
{
    [TestMethod]
    [DataRow("Read", """{"file_path":"/w/src/DataSync/README.md"}""", "📖 reading README.md")]
    [DataRow("Edit", """{"file_path":"/w/AGENTS.md"}""", "✏️ editing AGENTS.md")]
    [DataRow("Grep", """{"pattern":"MapGet"}""", "🔎 searching for MapGet")]
    [DataRow("Bash", """{"command":"dotnet test\nmore","description":"Run the tests"}""", "🔧 Run the tests")]
    [DataRow("Bash", """{"command":"dotnet build\nmore"}""", "🔧 dotnet build")]
    [DataRow("mcp__agentd__set_phase", "{}", "💬 set phase")]
    [DataRow("WebFetch", "not json", "⚙️ WebFetch")]
    public void Tool_calls_read_as_short_human_steps(string tool, string input, string expected) =>
        Assert.AreEqual(expected, ActivityText.Describe(tool, input));
}
