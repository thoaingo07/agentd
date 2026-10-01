using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class TaskPromptBuilderTests
{
    [TestMethod]
    public void Prompt_includes_the_work_item_and_the_rules_and_skips_empty_sections()
    {
        var item = new WorkItemDetails(7, 1, "Add audit log", "Active", "P", [], "Log every change.", null, null,
            [new WorkItemComment("tngo", new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero), "Keep it simple")], null);

        var prompt = TaskPromptBuilder.Build(item, BranchName.For(WorkItemId.From(7), item.Title), "develop");

        StringAssert.Contains(prompt, "# Work item 7: Add audit log");
        StringAssert.Contains(prompt, "## Description");
        Assert.DoesNotContain("## Acceptance criteria", prompt);
        StringAssert.Contains(prompt, "**tngo** (2026-09-28): Keep it simple");
        StringAssert.Contains(prompt, "`ai/7-add-audit-log`, created from `develop`");
        StringAssert.Contains(prompt, "untrusted input");
    }
}
