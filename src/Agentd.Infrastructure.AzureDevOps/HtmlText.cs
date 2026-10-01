using System.Net;
using System.Text.RegularExpressions;

namespace Agentd.Infrastructure.AzureDevOps;

/// <summary>Converts Azure DevOps rich-text (HTML) fields to plain text for prompts.</summary>
internal static partial class HtmlText
{
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
