using Agentd.Application.Messaging;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Claude;

/// <summary>Reads the tail of <c>{TranscriptRoot}/wi-{id}/transcript.jsonl</c> (written by <see cref="ClaudeCodeRunner"/>).</summary>
public sealed class TranscriptReader(IOptions<ClaudeOptions> options) : ITranscriptReader
{
    public async Task<string?> TailAsync(WorkItemId workItem, int lines, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Paths.Expand(options.Value.TranscriptRoot), $"wi-{workItem}", "transcript.jsonl");
        if (!File.Exists(path))
        {
            return null;
        }

        var tail = new Queue<string>(lines);
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (tail.Count == lines)
            {
                tail.Dequeue();
            }

            tail.Enqueue(line);
        }

        return string.Join('\n', tail);
    }
}
