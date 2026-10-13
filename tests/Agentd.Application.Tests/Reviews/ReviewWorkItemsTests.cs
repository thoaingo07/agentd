using Agentd.Application.Ports;
using Agentd.Application.Reviews;
using Agentd.Application.Tests.Fakes;

namespace Agentd.Application.Tests.Reviews;

[TestClass]
public sealed class ReviewWorkItemsTests
{
    private readonly FakeWorkItems _items = new();

    [TestMethod]
    [DataRow("ai/5617-deploy-to-aks", 5617)]
    [DataRow("feature/5617-x", 5617)]
    [DataRow("5617_fix", 5617)]
    [DataRow("5617", 5617)]
    [DataRow("feature/keyset-chunks", null)]
    [DataRow("release/1.2", null)]
    [DataRow("users/tngo/5617-x", null)]
    [DataRow(null, null)]
    public void A_branch_names_its_work_item(string? branch, int? expected) =>
        CollectionAssert.AreEqual(expected is { } id ? new[] { id } : Array.Empty<int>(), ReviewWorkItems.FromBranch(branch).ToArray());

    [TestMethod]
    public async Task A_tasks_parent_user_story_comes_right_after_it_once()
    {
        Item(5617, "Task", "Deploy the API", parent: 5600, ac: "Helm chart for the API");
        Item(5618, "Task", "Deploy the worker", parent: 5600);
        Item(5600, "User Story", "Deploy to AKS", ac: "- test deploys on merge to develop");

        var context = await new ReviewWorkItems(_items).ContextAsync([5617, 5618], default);

        StringAssert.Contains(context, "WI-5617 (Task) \"Deploy the API\" (Active)\nAcceptance criteria:\nHelm chart for the API");
        StringAssert.Contains(context, "Parent of WI-5617: WI-5600 (User Story) \"Deploy to AKS\" (Active)\nAcceptance criteria:\n- test deploys on merge to develop");
        Assert.AreEqual(1, context.Split("WI-5600 (User Story)").Length - 1, "the shared parent once");
        StringAssert.Contains(context, "WI-5618 (Task) \"Deploy the worker\" (Active)\nAcceptance criteria: none written.");
    }

    [TestMethod]
    public async Task A_linked_parent_shows_as_linked_and_stories_and_bugs_need_no_parent()
    {
        Item(5617, "Task", "Deploy the API", parent: 5600);
        Item(5600, "User Story", "Deploy to AKS", parent: 5000);
        Item(5000, "Feature", "Platform");

        var context = await new ReviewWorkItems(_items).ContextAsync([5617, 5600], default);

        Assert.DoesNotContain("Parent of", context);
        Assert.DoesNotContain("WI-5000", context, "only a task's parent");
    }

    [TestMethod]
    public async Task Missing_unreadable_or_no_work_items_are_said_plainly()
    {
        var none = await new ReviewWorkItems(_items).ContextAsync([], default);
        var noSource = await new ReviewWorkItems().ContextAsync([5617], default);
        var missing = await new ReviewWorkItems(_items).ContextAsync([1, 2, 3, 4], default);

        StringAssert.Contains(none, "Linked work items: none");
        StringAssert.Contains(noSource, "Linked work items: none");
        StringAssert.Contains(missing, "WI-1: not found.");
        Assert.DoesNotContain("WI-4", missing, $"at most {ReviewWorkItems.MaxLinked}");
    }

    private void Item(int id, string type, string title, int? parent = null, string? ac = null) =>
        _items.Items[id] = new WorkItemDetails(id, 1, title, "Active", @"Portal\sysmin", [], null, ac, null, [], null, Type: type, ParentId: parent);
}
