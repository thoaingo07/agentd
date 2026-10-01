using System.Text;

namespace Agentd.Host.Cli.Commands;

/// <summary>Left-aligned columns separated by two spaces; the last column is not padded.</summary>
internal static class TextTable
{
    public static string Render(IReadOnlyList<string[]> rows)
    {
        var widths = Enumerable.Range(0, rows[0].Length).Select(c => rows.Max(r => r[c].Length)).ToArray();
        var sb = new StringBuilder();
        var line = new StringBuilder();
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length; c++)
            {
                line.Append(c == row.Length - 1 ? row[c] : row[c].PadRight(widths[c] + 2));
            }

            sb.Append(line.ToString().TrimEnd()).Append('\n');
            line.Clear();
        }

        return sb.ToString();
    }
}
