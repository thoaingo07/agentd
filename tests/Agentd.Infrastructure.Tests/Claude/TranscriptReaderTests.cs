using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Infrastructure.Claude;

namespace Agentd.Infrastructure.Tests.Claude;

[TestClass]
public sealed class TranscriptReaderTests
{
    [TestMethod]
    public async Task Returns_the_last_lines_or_null_without_a_transcript()
    {
        var root = Directory.CreateTempSubdirectory("agentd-transcripts-").FullName;
        try
        {
            var reader = new TranscriptReader(Microsoft.Extensions.Options.Options.Create(new ClaudeOptions { TranscriptRoot = root }));
            Assert.IsNull(await reader.TailAsync(WorkItemId.From(7), 2, default));

            Directory.CreateDirectory(Path.Combine(root, "wi-7"));
            await File.WriteAllLinesAsync(Path.Combine(root, "wi-7", "transcript.jsonl"), ["l1", "l2", "l3", "l4", "l5"]);

            Assert.AreEqual("l4\nl5", await reader.TailAsync(WorkItemId.From(7), 2, default));
            Assert.AreEqual("l1\nl2\nl3\nl4\nl5", await reader.TailAsync(WorkItemId.From(7), 200, default));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
