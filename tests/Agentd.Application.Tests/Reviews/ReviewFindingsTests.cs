using Agentd.Application.Reviews;

namespace Agentd.Application.Tests.Reviews;

[TestClass]
public sealed class ReviewFindingsTests
{
    private const string Valid = """
        Two problems, one of them serious.

        ```review-findings
        {"summary":"Adds a health endpoint. Mostly fine, one real bug.","findings":[
          {"severity":"Breaks","file":"/src/Api/Health.cs","line":42,"title":"Readiness never fails","detail":"The DB check swallows exceptions.","suggestion":"Return Unhealthy on failure."},
          {"severity":"performance","title":"Health checks run on every request"}]}
        ```
        """;

    [TestMethod]
    public void A_valid_block_is_parsed_normalized_and_removed_from_the_text()
    {
        var (text, result, problem) = ReviewFindings.Extract(Valid);

        Assert.IsNull(problem);
        Assert.AreEqual("Two problems, one of them serious.", text);
        Assert.HasCount(2, result!.Findings);
        Assert.AreEqual(("breaks", "src/Api/Health.cs", (int?)42), (result.Findings[0].Severity, result.Findings[0].File, result.Findings[0].Line), "lower-cased severity, repo-relative path");
        Assert.IsNull(result.Findings[1].File, "a PR-wide finding");
    }

    [TestMethod]
    [DataRow("""{"summary":"","findings":[]}""", "summary")]
    [DataRow("""{"summary":"s","findings":[{"severity":"major","title":"t"}]}""", "severity")]
    [DataRow("""{"summary":"s","findings":[{"severity":"breaks","title":""}]}""", "title")]
    [DataRow("""{"summary":"s","findings":[{"severity":"breaks","title":"t","file":"../etc/passwd"}]}""", "inside the repository")]
    [DataRow("""{"summary":"s","findings":[{"severity":"breaks","title":"t","file":"C:/x.cs"}]}""", "inside the repository")]
    [DataRow("""{"summary":"s","findings":[{"severity":"breaks","title":"t","file":"a.cs","line":0}]}""", "line")]
    [DataRow("""{"summary":"s","findings":[""", "valid JSON")]
    public void A_broken_block_is_reported_back(string json, string expected)
    {
        var (_, result, problem) = ReviewFindings.Extract($"x\n```review-findings\n{json}\n```");

        Assert.IsNull(result);
        StringAssert.Contains(problem, expected);
    }

    [TestMethod]
    public void No_block_is_just_conversation()
    {
        Assert.AreEqual(("Why is #2 a problem?", (ReviewResult?)null, (string?)null), ReviewFindings.Extract(" Why is #2 a problem? "));
    }

    [TestMethod]
    public void Findings_render_short_with_where_why_and_the_fix()
    {
        var text = ReviewFindings.Render(ReviewFindings.Extract(Valid).Result!);

        StringAssert.StartsWith(text, "🔍 **Review:** Adds a health endpoint.");
        StringAssert.Contains(text, "**1.** 🔴 **Readiness never fails** · `src/Api/Health.cs:42`");
        StringAssert.Contains(text, "   The DB check swallows exceptions.");
        StringAssert.Contains(text, "   **Fix:** Return Unhealthy on failure.");
        StringAssert.Contains(text, "**2.** 🟠 **Health checks run on every request**");
        StringAssert.Contains(text, ReviewFindings.Legend);
        StringAssert.Contains(ReviewFindings.Render(new ReviewResult("Looks good.", [])), "Nothing that breaks the app");
    }

    [TestMethod]
    public void A_line_thread_is_the_title_why_and_fix_only()
    {
        var finding = ReviewFindings.Extract(Valid).Result!.Findings[0];

        Assert.AreEqual("🔴 **Readiness never fails**\nThe DB check swallows exceptions.\n**Fix:** Return Unhealthy on failure.", ReviewFindings.ThreadText(finding));
    }

    [TestMethod]
    public void The_main_message_lists_every_finding_with_its_status()
    {
        var findings = ReviewFindings.Extract(Valid).Result!.Findings;
        var result = new ReviewResult("Adds a health endpoint.", [findings[0] with { Status = ReviewFindings.Fixed }, findings[1] with { Status = ReviewFindings.Open }, findings[1] with { Title = "Old | idea", Status = ReviewFindings.Closed }]);

        var text = ReviewFindings.MainMessage(result, "b7e9f01c2d");

        StringAssert.StartsWith(text, "🤖 **agentd review** · 3 finding(s): 1 open, 1 fixed · checked at b7e9f01");
        StringAssert.Contains(text, "| ✅ | Readiness never fails | `src/Api/Health.cs:42` |");
        StringAssert.Contains(text, "| 🟠 | Health checks run on every request |  |");
        StringAssert.Contains(text, "| ⚪ | Old \\| idea |  |", "a pipe can't break the table");
        StringAssert.Contains(text, "agentd checks again after every push");
    }

    [TestMethod]
    public void Older_reviews_still_get_an_icon()
    {
        Assert.AreEqual(("🔴", "🔴", "🟠", "🟠"), (ReviewFindings.Icon("blocker"), ReviewFindings.Icon("major"), ReviewFindings.Icon("minor"), ReviewFindings.Icon("nit")));
    }
}
