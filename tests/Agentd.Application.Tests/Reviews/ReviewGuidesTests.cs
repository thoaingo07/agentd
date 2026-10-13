using Agentd.Application.Reviews;

namespace Agentd.Application.Tests.Reviews;

[TestClass]
public sealed class ReviewGuidesTests
{
    private static ReviewGuide Guide(params GuideChapter[] chapters) => new("What it does.", chapters);

    private static GuideChapter Chapter(string title, params string[] files) => new(title, "why", [.. files.Select(f => new GuideFile(f, $"{f} changed"))]);

    [TestMethod]
    [DataRow("no block at all", null, null)]
    [DataRow("```review-guide\n{\"overview\":\"o\",\"chapters\":[{\"title\":\"t\",\"files\":[]}]}\n```", "o", null)]
    [DataRow("```review-guide\n{not json}\n```", null, "isn't valid JSON")]
    [DataRow("```review-guide\n{\"overview\":\" \",\"chapters\":[{\"title\":\"t\",\"files\":[]}]}\n```", null, "needs an overview")]
    [DataRow("```review-guide\n{\"overview\":\"o\",\"chapters\":[]}\n```", null, "1 to 10 chapters")]
    [DataRow("```review-guide\n{\"overview\":\"o\",\"chapters\":[{\"title\":\"\",\"files\":[]}]}\n```", null, "every chapter needs a title")]
    [DataRow("```review-guide\n{\"overview\":\"o\",\"chapters\":[{\"title\":\"t\",\"files\":[]}],\"notes\":[{\"file\":\"a\",\"line\":0,\"text\":\"x\"}]}\n```", null, "every note needs a line")]
    [DataRow("```review-guide\n{\"overview\":\"o\",\"chapters\":[{\"title\":\"t\",\"files\":[]}],\"suggestions\":[{\"title\":\" \"}]}\n```", null, "every suggestion needs a title")]
    public void The_guide_block_is_found_and_checked(string reply, string? overview, string? problem)
    {
        var (guide, why) = ReviewGuides.Extract("Findings above.\n\n" + reply);

        Assert.AreEqual(overview, guide?.Overview);
        if (problem is null)
        {
            Assert.IsNull(why);
        }
        else
        {
            StringAssert.Contains(why, problem);
        }
    }

    [TestMethod]
    public void Too_many_chapters_notes_or_suggestions_are_refused()
    {
        var chapters = Enumerable.Range(0, ReviewGuides.MaxChapters + 1).Select(i => Chapter($"c{i}")).ToArray();
        var notes = Enumerable.Range(0, ReviewGuides.MaxNotes + 1).Select(i => new GuideNote("a.cs", 1, "x")).ToList();
        var suggestions = Enumerable.Range(0, ReviewGuides.MaxSuggestions + 1).Select(i => new GuideSuggestion(null, null, "s", null, null)).ToList();

        StringAssert.Contains(ReviewGuides.Problem(Guide(chapters)), "chapters");
        StringAssert.Contains(ReviewGuides.Problem(Guide(Chapter("c")) with { Notes = notes }), "notes");
        StringAssert.Contains(ReviewGuides.Problem(Guide(Chapter("c")) with { Suggestions = suggestions }), "suggestions");
    }

    [TestMethod]
    public void Fitting_puts_every_changed_file_in_exactly_one_chapter_in_the_reviewers_order()
    {
        var guide = Guide(Chapter(" Heart ", "src/B.cs", "/src/A.cs"), Chapter("Again", "src/A.cs", "src/Gone.cs"), Chapter("Glue", "docs/x.md")) with
        {
            Notes = [new GuideNote("src/A.cs", 3, " explains "), new GuideNote("src/Gone.cs", 1, "not in the change")],
            Suggestions = [new GuideSuggestion("src/Gone.cs", 4, "elsewhere", null, null), new GuideSuggestion("/src/B.cs", 9, " here ", null, null)],
        };

        var fitted = ReviewGuides.Fit(guide, ["src/A.cs", "src/B.cs", "src/C.cs", "docs/x.md"]);

        CollectionAssert.AreEqual(new[] { "Heart", "Glue", ReviewGuides.Rest }, fitted.Chapters.Select(c => c.Title).ToList(), "a chapter left with no changed files goes");
        CollectionAssert.AreEqual(new[] { "src/B.cs", "src/A.cs" }, fitted.Chapters[0].Files.Select(f => f.Path).ToList(), "the reviewer's order, first chapter wins");
        Assert.AreEqual(("src/C.cs", (string?)null), (fitted.Chapters[2].Files.Single().Path, fitted.Chapters[2].Files.Single().Summary));
        Assert.AreEqual(("src/A.cs", "explains"), (fitted.Notes!.Single().File, fitted.Notes!.Single().Text), "notes only where the diff shows them");
        Assert.AreEqual(((string?)null, (int?)null, "elsewhere"), (fitted.Suggestions![0].File, fitted.Suggestions[0].Line, fitted.Suggestions[0].Title), "kept, without a place outside the change");
        Assert.AreEqual(("src/B.cs", (int?)9, "here"), (fitted.Suggestions[1].File, fitted.Suggestions[1].Line, fitted.Suggestions[1].Title));
    }

    [TestMethod]
    public void A_guide_naming_no_changed_file_still_lists_them_all()
    {
        var fitted = ReviewGuides.Fit(Guide(Chapter("Other", "src/Gone.cs")), ["src/A.cs"]);

        Assert.AreEqual(ReviewGuides.Rest, fitted.Chapters.Single().Title);
        Assert.AreEqual("src/A.cs", fitted.Chapters.Single().Files.Single().Path);
    }
}
