using System.Text;
using Agentd.Application.Messaging;

namespace Agentd.Application.Tests.Messaging;

[TestClass]
public sealed class MessageChunkerTests
{
    private static readonly MessagingCapabilities s_discord = new(2000, true, true, true, true);

    [TestMethod]
    public void Short_text_is_unchanged() =>
        CollectionAssert.AreEqual(new[] { "hello **world**" }, MessageChunker.Split("hello **world**", 2000).ToArray());

    [TestMethod]
    public void Text_exactly_at_the_budget_is_one_part()
    {
        var text = new string('a', 2000 - MessageChunker.RenderHeadroom);

        Assert.HasCount(1, MessageChunker.Split(text, 2000));
        Assert.HasCount(2, MessageChunker.Split(text + "a", 2000));
    }

    [TestMethod]
    public void Paragraphs_are_kept_whole_and_parts_are_numbered()
    {
        var paragraphs = Enumerable.Range(1, 6).Select(i => $"Paragraph {i}: " + new string('x', 700)).ToList();

        var parts = MessageChunker.Split(string.Join("\n\n", paragraphs), 2000);

        Assert.HasCount(3, parts);
        StringAssert.EndsWith(parts[0], "(1/3)");
        StringAssert.EndsWith(parts[2], "(3/3)");
        StringAssert.StartsWith(parts[1], "Paragraph 3:");
    }

    [TestMethod]
    public void A_code_block_spanning_parts_is_refenced_with_its_language()
    {
        var code = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"var line{i} = {i};"));

        var parts = MessageChunker.Split($"Intro\n\n```csharp\n{code}\n```\n\nOutro", 2000);

        Assert.IsGreaterThan(1, parts.Count);
        foreach (var part in parts.Where(p => p.Contains("var line", StringComparison.Ordinal)))
        {
            StringAssert.Contains(part, "```csharp\n");
            Assert.AreEqual(0, FenceCount(part) % 2, "fences are balanced in every part");
        }
    }

    [TestMethod]
    public void Long_text_becomes_an_attachment_when_supported()
    {
        var message = new OutboundMessage(MessageKind.Result, new string('y', 9000));

        var prepared = MessageChunker.Prepare(message, s_discord).Single();

        Assert.AreEqual("message.md", prepared.Attachments!.Single().FileName);
        Assert.AreEqual(9000, prepared.Attachments![0].Content.Length);

        var split = MessageChunker.Prepare(message, s_discord with { SupportsAttachments = false });
        Assert.IsGreaterThan(1, split.Count);
    }

    [TestMethod]
    public void Options_stay_on_the_last_part()
    {
        var message = new OutboundMessage(MessageKind.Question, string.Join("\n\n", Enumerable.Range(0, 5).Select(_ => new string('q', 900))), [new MessageOption("yes", "Yes")]);

        var parts = MessageChunker.Prepare(message, s_discord with { SupportsAttachments = false });

        Assert.IsNull(parts[0].Options);
        Assert.HasCount(1, parts[^1].Options!);
    }

    [TestMethod]
    [DataRow(2000)]
    [DataRow(4096)]
    public void Random_markdown_always_fits_with_balanced_fences_and_no_lost_text(int limit)
    {
        var random = new Random(limit);
        for (var run = 0; run < 300; run++)
        {
            var markdown = RandomMarkdown(random);

            var parts = MessageChunker.Split(markdown, limit);

            foreach (var part in parts)
            {
                Assert.IsLessThanOrEqualTo(limit - MessageChunker.RenderHeadroom, part.Length, $"run {run}");
                Assert.AreEqual(0, FenceCount(part) % 2, $"run {run}: unbalanced fences");
            }

            Assert.AreEqual(Content(markdown), Content(string.Join("\n", parts.Select(StripMarker))), $"run {run}: text changed");
        }
    }

    private static string RandomMarkdown(Random random)
    {
        var sb = new StringBuilder();
        var blocks = random.Next(1, 40);
        for (var b = 0; b < blocks; b++)
        {
            if (random.Next(4) == 0)
            {
                sb.Append("```").Append(random.Next(2) == 0 ? "ts" : "").Append('\n');
                for (var l = random.Next(1, 120); l > 0; l--)
                {
                    sb.Append(Words(random, random.Next(1, 30))).Append('\n');
                }

                sb.Append("```\n\n");
            }
            else
            {
                sb.Append(random.Next(10) == 0 ? new string('z', random.Next(100, 6000)) : Words(random, random.Next(1, 300))).Append("\n\n");
            }
        }

        return sb.ToString();
    }

    private static string Words(Random random, int count) =>
        string.Join(' ', Enumerable.Range(0, count).Select(_ => new string((char)('a' + random.Next(26)), random.Next(1, 12))));

    private static int FenceCount(string part) => part.Split('\n').Count(l => l.TrimStart().StartsWith("```", StringComparison.Ordinal));

    private static string StripMarker(string part) => System.Text.RegularExpressions.Regex.Replace(part, @"\n\n\(\d+/\d+\)$", "");

    /// <summary>The text without fences and whitespace: splitting may move line breaks and add fences, nothing else.</summary>
    private static string Content(string markdown) =>
        string.Concat(markdown.Split('\n').Where(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal)).SelectMany(l => l.Where(c => !char.IsWhiteSpace(c))));
}
