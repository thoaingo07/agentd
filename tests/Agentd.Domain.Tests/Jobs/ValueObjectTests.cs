using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Domain.Tests.Jobs;

[TestClass]
public sealed class ValueObjectTests
{
    [TestMethod]
    [DataRow("Fix login redirect", "ai/1234-fix-login-redirect")]
    [DataRow("  Ünïcödé   Tìtle!! ", "ai/1234-unicode-title")]
    [DataRow("feat: add audit_log (v2) / API", "ai/1234-feat-add-audit-log-v2-api")]
    [DataRow("", "ai/1234")]
    [DataRow("!!!", "ai/1234")]
    public void BranchName_slugifies_titles(string title, string expected) =>
        Assert.AreEqual(expected, BranchName.For(WorkItemId.From(1234), title).Value);

    [TestMethod]
    public void BranchName_slug_is_capped_without_trailing_dash()
    {
        var branch = BranchName.For(WorkItemId.From(1), new string('a', 39) + " bbbbbbbbbb").Value;
        var slug = branch["ai/1-".Length..];

        Assert.IsLessThanOrEqualTo(BranchName.MaxSlugLength, slug.Length);
        Assert.IsFalse(slug.EndsWith('-'));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-5)]
    public void WorkItemId_must_be_positive(int value) =>
        Assert.AreEqual("validation", WorkItemId.Create(value).Error?.Code);

    [TestMethod]
    public void PullRequestDraft_requires_a_title_within_limit()
    {
        Assert.IsFalse(PullRequestDraft.Create(" ", null, null).IsSuccess);
        Assert.IsFalse(PullRequestDraft.Create(new string('x', 201), null, null).IsSuccess);
        Assert.AreEqual("Ok", PullRequestDraft.Create(" Ok ", null, null).Value!.Title);
    }
}
