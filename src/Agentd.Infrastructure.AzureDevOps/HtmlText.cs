using System.Net;
using System.Text.RegularExpressions;

namespace Agentd.Infrastructure.AzureDevOps;

/// <summary>Converts Azure DevOps rich-text (HTML) fields to plain text for prompts.</summary>
internal static partial class HtmlText
{
    /// <summary>
    /// Plain text as simple, escaped HTML for rich-text fields: blank lines separate paragraphs, and runs of
    /// "- " lines become a list. Null or blank stays null. Nothing is passed through as raw HTML.
    /// </summary>
    public static string? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var html = new System.Text.StringBuilder();
        foreach (var paragraph in text.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n"))
        {
            var prose = new List<string>();
            var bullets = new List<string>();
            void Flush()
            {
                if (prose.Count > 0)
                {
                    html.Append("<p>").AppendJoin("<br>", prose.Select(System.Net.WebUtility.HtmlEncode)).Append("</p>");
                    prose.Clear();
                }

                if (bullets.Count > 0)
                {
                    html.Append("<ul>").AppendJoin(string.Empty, bullets.Select(b => "<li>" + System.Net.WebUtility.HtmlEncode(b) + "</li>")).Append("</ul>");
                    bullets.Clear();
                }
            }

            foreach (var line in paragraph.Split('\n').Select(l => l.TrimEnd()))
            {
                if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                {
                    if (prose.Count > 0)
                    {
                        Flush();
                    }

                    bullets.Add(line.TrimStart()[2..]);
                }
                else
                {
                    if (bullets.Count > 0)
                    {
                        Flush();
                    }

                    prose.Add(line);
                }
            }

            Flush();
        }

        return html.ToString();
    }

    public static string? ToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = BlockTags().Replace(html, "\n");
        text = ListItem().Replace(text, "\n- ");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = BlankLines().Replace(text.Replace("\r", string.Empty, StringComparison.Ordinal), "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/h[1-6]|/tr)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<\s*li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex BlankLines();
}
