using Agentd.Application.Ports;
using Agentd.Application.Repositories;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;

namespace Agentd.Application.Tests.Repositories;

[TestClass]
public sealed class RepositoryMatcherTests
{
    private static readonly Repository s_portal = Repo("portal", "repo:portal", "Portal");
    private static readonly Repository s_platform = Repo("platform", "repo:platform", "Portal\\Platform");

    [TestMethod]
    public void Tag_wins_over_area_path() =>
        Assert.AreEqual("portal", Matched(Item("Portal\\Platform", "repo:portal")));

    [TestMethod]
    public void Longest_area_path_prefix_wins() =>
        Assert.AreEqual("platform", Matched(Item("Portal\\Platform\\Auth")));

    [TestMethod]
    public void Area_path_matching_is_case_insensitive_and_accepts_forward_slashes() =>
        Assert.AreEqual("platform", Matched(Item("portal/platform")));

    [TestMethod]
    public void Prefix_must_end_at_a_path_boundary() =>
        Assert.AreEqual("portal", Matched(Item("Portal\\Platform2")));

    [TestMethod]
    public void No_rule_matches() =>
        Assert.IsInstanceOfType<RepositoryMatch.NoMatch>(RepositoryMatcher.Match([s_portal, s_platform], Item("Other")));

    [TestMethod]
    public void Two_repositories_with_the_same_tag_are_ambiguous()
    {
        var twin = Repo("portal-2", "repo:portal", "Elsewhere");

        var match = RepositoryMatcher.Match([s_portal, twin], Item("X", "repo:portal"));

        CollectionAssert.AreEqual(new List<string> { "portal", "portal-2" }, ((RepositoryMatch.Ambiguous)match).Candidates.ToList());
    }

    private static string Matched(WorkItemDetails item) =>
        ((RepositoryMatch.Matched)RepositoryMatcher.Match([s_portal, s_platform], item)).Repository.Name.Value;

    private static Repository Repo(string name, string tag, string areaPath) =>
        new(RepositoryName.From(name), $"git@x:v3/o/p/{name}", new AzureDevOpsRepo("o", "p", name), "develop", tag, [areaPath]);

    private static WorkItemDetails Item(string areaPath, params string[] tags) =>
        new(1, 1, "t", "Active", areaPath, tags, null, null, null, [], null);
}
