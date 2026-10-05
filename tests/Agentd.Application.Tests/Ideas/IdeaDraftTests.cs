using Agentd.Application.Ideas;

namespace Agentd.Application.Tests.Ideas;

[TestClass]
public sealed class IdeaDraftTests
{
    [TestMethod]
    public void A_work_items_block_becomes_drafts_and_leaves_the_text()
    {
        var reply = "Here's a plan.\n\n```work-items\n[{\"type\":\"User Story\",\"title\":\"Dark mode\",\"acceptanceCriteria\":\"Toggle in settings\",\"estimate\":5}," +
            "{\"type\":\"Task\",\"title\":\"Theme tokens\",\"estimate\":4,\"parent\":0},{\"type\":\"Task\",\"title\":\"Toggle UI\",\"estimate\":3,\"parent\":0}]\n```";

        var (text, drafts, problem) = WorkItemDrafts.Extract(reply);

        Assert.AreEqual("Here's a plan.", text);
        Assert.IsNull(problem);
        Assert.HasCount(3, drafts!);
        var rendered = WorkItemDrafts.Render(drafts!);
        StringAssert.Contains(rendered, "1. **User Story: Dark mode** · 5 pts");
        StringAssert.Contains(rendered, "      2. **Task: Theme tokens** · 4 h", "tasks sit under their story");
        StringAssert.Contains(rendered, "✓ Toggle in settings");
    }

    [TestMethod]
    [DataRow("```work-items\nnot json\n```", "valid JSON")]
    [DataRow("```work-items\n[]\n```", "empty")]
    [DataRow("```work-items\n[{\"type\":\"Epic\",\"title\":\"x\"}]\n```", "User Story or Task")]
    [DataRow("```work-items\n[{\"type\":\"Task\",\"title\":\"a\",\"parent\":3}]\n```", "parent")]
    public void Broken_blocks_are_reported_not_shown(string reply, string problem)
    {
        var (_, drafts, found) = WorkItemDrafts.Extract(reply);

        Assert.IsNull(drafts);
        StringAssert.Contains(found, problem);
    }

    [TestMethod]
    public void Idea_options_come_off_the_text()
    {
        var parsed = BrainstormSettings.Parse("--model opus dark mode for the --effort high portal --repo sysmin".Split(' '));

        Assert.AreEqual(("dark mode for the portal", "sysmin", "opus", "high", (string?)null), parsed);
        StringAssert.Contains(BrainstormSettings.Parse(["--effort", "turbo", "x"]).Problem, "isn't an effort level");
        StringAssert.Contains(BrainstormSettings.Parse(["--model", "rm -rf", "x"]).Problem, "isn't a model name");
    }
}
