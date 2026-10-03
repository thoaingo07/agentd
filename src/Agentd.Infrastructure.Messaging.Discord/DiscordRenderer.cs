using System.Text;
using System.Text.RegularExpressions;
using Agentd.Application.Messaging;

namespace Agentd.Infrastructure.Messaging.Discord;

/// <summary>
/// Neutral Markdown → Discord markdown. The syntax is shared, so formatting passes through; what
/// changes is safety: <c>@everyone</c>/<c>@here</c> and <c>&lt;@id&gt;</c>-style mentions render as
/// inert text, links render only for http(s), and a leading <c>#</c> doesn't become a heading.
/// Code blocks are left untouched. The real guarantee against pings is <c>allowed_mentions</c>,
/// which the provider always sends empty.
/// </summary>
public static partial class DiscordRenderer
{
    private const char ZeroWidthSpace = '​';

    public static string Render(OutboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var text = RenderMarkdown(message.Markdown);
        if (message.Options is { Count: > 0 } options)
        {
            // No buttons without the Gateway: numbered answers instead (the poller maps "2" back).
            var list = string.Join('\n', options.Select((o, i) => $"{i + 1}. {RenderMarkdown(o.Label)}"));
            text += $"\n\nReply with a number:\n{list}";
        }

        return text;
    }

    public static string RenderMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var sb = new StringBuilder();
        var inCode = false;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                sb.Append(line).Append('\n');
                continue;
            }

            sb.Append(inCode ? line : Inert(line)).Append('\n');
        }

        return sb.ToString(0, Math.Max(0, sb.Length - 1));
    }

    private static string Inert(string line)
    {
        line = MassMention().Replace(line, m => $"@{ZeroWidthSpace}{m.Groups[1].Value}");
        line = Mention().Replace(line, m => $"<{ZeroWidthSpace}{m.Groups[1].Value}>");
        line = Link().Replace(line, m => IsWebUrl(m.Groups[2].Value)
            ? m.Value
            : $"\\[{m.Groups[1].Value}\\] ({m.Groups[2].Value})");
        return Heading().Replace(line, "\\$1");
    }

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    [GeneratedRegex(@"@(everyone|here)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MassMention();

    [GeneratedRegex(@"<((?:@[!&]?|#)\d+)>")]
    private static partial Regex Mention();

    [GeneratedRegex(@"\[([^\]]*)\]\(([^)\s]*)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^(\s*-?#)")]
    private static partial Regex Heading();
}
