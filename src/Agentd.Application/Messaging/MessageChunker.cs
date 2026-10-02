using System.Globalization;
using System.Text;

namespace Agentd.Application.Messaging;

/// <summary>
/// Fits a neutral message to a provider (at dispatch time, so the outbox keeps the unsplit message):
/// long content becomes an attachment when the provider supports it, then the text is split into
/// parts on paragraph and line boundaries, never inside a fenced code block (an oversized block is
/// closed and reopened with its language), and numbered <c>(1/3)</c>.
/// </summary>
public static class MessageChunker
{
    /// <summary>Headroom for rendering: escaping can make the rendered text longer than the Markdown.</summary>
    public const int RenderHeadroom = 64;

    /// <summary>Above this, text is sent as an attachment when the provider supports attachments.</summary>
    public const int AttachmentThreshold = 8000;

    /// <summary>Room kept for the <c>\n\n(12/34)</c> part marker.</summary>
    private const int MarkerReserve = 12;

    private const string Fence = "```";

    /// <summary>The messages to send for <paramref name="message"/> on a provider with <paramref name="capabilities"/>.</summary>
    public static IReadOnlyList<OutboundMessage> Prepare(OutboundMessage message, MessagingCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.SupportsAttachments && message.Markdown.Length > AttachmentThreshold)
        {
            var file = new Attachment("message.md", "text/markdown", Encoding.UTF8.GetBytes(message.Markdown));
            var summary = $"The full text ({message.Markdown.Length.ToString(CultureInfo.InvariantCulture)} characters) is attached.";
            return [message with { Markdown = summary, Attachments = [.. message.Attachments ?? [], file] }];
        }

        var parts = Split(message.Markdown, capabilities.MaxMessageLength);
        // Buttons and attachments go with the last part, so they follow the full text.
        return parts.Select((p, i) => i == parts.Count - 1
                ? message with { Markdown = p }
                : new OutboundMessage(message.Kind, p))
            .ToList();
    }

    /// <summary>Splits Markdown into parts of at most <c>maxLength - RenderHeadroom</c> characters (markers included).</summary>
    public static IReadOnlyList<string> Split(string markdown, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var budget = maxLength - RenderHeadroom - MarkerReserve;
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, 32, nameof(maxLength));
        if (markdown.Length <= maxLength - RenderHeadroom)
        {
            return [markdown];
        }

        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var piece in Blocks(markdown).SelectMany(b => Fit(b, budget)))
        {
            if (current.Length > 0 && current.Length + 2 + piece.Length > budget)
            {
                parts.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append("\n\n");
            }

            current.Append(piece);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts.Count == 1
            ? parts
            : parts.Select((p, i) => string.Create(CultureInfo.InvariantCulture, $"{p}\n\n({i + 1}/{parts.Count})")).ToList();
    }

    /// <summary>Paragraphs (split on blank lines) and whole fenced code blocks.</summary>
    private static IEnumerable<Block> Blocks(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var paragraph = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith(Fence, StringComparison.Ordinal))
            {
                if (paragraph.Count > 0)
                {
                    yield return new Block(paragraph.ToList(), null);
                    paragraph.Clear();
                }

                var language = lines[i].TrimStart()[Fence.Length..].Trim();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(Fence, StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i]);
                }

                yield return new Block(code, language);
            }
            else if (string.IsNullOrWhiteSpace(lines[i]))
            {
                if (paragraph.Count > 0)
                {
                    yield return new Block(paragraph.ToList(), null);
                    paragraph.Clear();
                }
            }
            else
            {
                paragraph.Add(lines[i]);
            }
        }

        if (paragraph.Count > 0)
        {
            yield return new Block(paragraph, null);
        }
    }

    /// <summary>A block as one or more pieces that each fit the budget.</summary>
    private static IEnumerable<string> Fit(Block block, int budget)
    {
        var open = block.Language is null ? "" : Fence + block.Language + "\n";
        var close = block.Language is null ? "" : "\n" + Fence;
        var room = budget - open.Length - close.Length;
        var piece = new StringBuilder();
        foreach (var line in block.Lines.SelectMany(l => HardWrap(l, room)))
        {
            if (piece.Length > 0 && piece.Length + 1 + line.Length > room)
            {
                yield return open + piece + close;
                piece.Clear();
            }

            if (piece.Length > 0)
            {
                piece.Append('\n');
            }

            piece.Append(line);
        }

        yield return open + piece + close;
    }

    /// <summary>Splits a single over-long line, preferring the last space before the limit.</summary>
    private static IEnumerable<string> HardWrap(string line, int room)
    {
        while (line.Length > room)
        {
            var cut = line.LastIndexOf(' ', room - 1);
            cut = cut <= room / 2 ? room : cut + 1;
            yield return line[..cut].TrimEnd();
            line = line[cut..];
        }

        yield return line;
    }

    private sealed record Block(IReadOnlyList<string> Lines, string? Language);
}
