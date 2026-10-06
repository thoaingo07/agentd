using Agentd.Application.Reviews;

namespace Agentd.Application.Tests.Reviews;

[TestClass]
public sealed class ReviewFindingsTests
{
    private const string Valid = """
        Two problems, one of them serious.

        ```review-findings
        {"summary":"Adds a health endpoint. Mostly fine, one real bug.","findings":[
          {"severity":"Major","file":"/src/Api/Health.cs","line":42,"title":"Readiness never fails","detail":"The DB check swallows exceptions.","suggestion":"Return Unhealthy on failure."},
          {"severity":"nit","title":"PR description is empty"}]}
        ```
        """;

    [TestMethod]
    public void A_valid_block_is_parsed_normalized_and_removed_from_the_text()
    {
        var (text, result, problem) = ReviewFindings.Extract(Valid);

        Assert.IsNull(problem);
        Assert.AreEqual("Two problems, one of them serious.", text);
        Assert.HasCount(2, result!.Findings);
        Assert.AreEqual(("major", "src/Api/Health.cs", (int?)42), (result.Findings[0].Severity, result.Findings[0].File, result.Findings[0].Line), "lower-cased severity, repo-relative path");
        Assert.IsNull(result.Findings[1].File, "a PR-wide finding");
    }

    [TestMethod]
    [DataRow("""{"summary":"","findings":[]}""", "summary")]
    [DataRow("""{"summary":"s","findings":[{"severity":"critical","title":"t"}]}""", "severity")]
    [DataRow("""{"summary":"s","findings":[{"severity":"minor","title":""}]}""", "title")]
    [DataRow("""{"summary":"s","findings":[{"severity":"minor","title":"t","file":"../etc/passwd"}]}""", "inside the repository")]
    [DataRow("""{"summary":"s","findings":[{"severity":"minor","title":"t","file":"C:/x.cs"}]}""", "inside the repository")]
    [DataRow("""{"summary":"s","findings":[{"severity":"minor","title":"t","file":"a.cs","line":0}]}""", "line")]
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
    public void Findings_render_numbered_with_severity_and_location()
    {
        var text = ReviewFindings.Render(ReviewFindings.Extract(Valid).Result!);

        StringAssert.StartsWith(text, "🔍 **Review summary:** Adds a health endpoint.");
        StringAssert.Contains(text, "**1.** 🟠 major **Readiness never fails** · `src/Api/Health.cs:42`");
        StringAssert.Contains(text, "💡 Return Unhealthy on failure.");
        StringAssert.Contains(text, "**2.** ⚪ nit **PR description is empty**");
        StringAssert.Contains(ReviewFindings.Render(new ReviewResult("Looks good.", [])), "No findings");
    }
}
