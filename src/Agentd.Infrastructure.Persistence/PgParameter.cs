using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence;

/// <summary>
/// Routine parameters. PostgreSQL can't store U+0000: <c>jsonb</c> refuses the <c>\u0000</c> escape (22P05) and
/// <c>text</c> the character itself (22021). Agent output carries it whenever a tool reads a binary or UTF-16 file, so
/// strings bound as either become U+FFFD instead of failing the write (and with it the job).
/// </summary>
public static class PgParameter
{
    private const string NulEscape = "\\u0000";

    public static NpgsqlParameter Of(object? value, NpgsqlDbType type) => new()
    {
        Value = value switch
        {
            null => DBNull.Value,
            string s when type == NpgsqlDbType.Jsonb => Json(s),
            string s when type == NpgsqlDbType.Text => Text(s),
            _ => value,
        },
        NpgsqlDbType = type,
    };

    /// <summary>The text with U+0000 as U+FFFD.</summary>
    public static string Text(string text) => text.Contains('\0', StringComparison.Ordinal) ? text.Replace('\0', '�') : text;

    /// <summary>The JSON with each <c>\u0000</c> escape as <c>�</c>; an escaped backslash followed by <c>u0000</c> stays as it is.</summary>
    public static string Json(string json)
    {
        json = Text(json);
        var at = json.IndexOf(NulEscape, StringComparison.Ordinal);
        if (at < 0)
        {
            return json;
        }

        var clean = new StringBuilder(json.Length);
        var from = 0;
        for (; at >= 0; at = json.IndexOf(NulEscape, at + 1, StringComparison.Ordinal))
        {
            // An escape only when the backslash isn't itself escaped: an odd run of backslashes ends at it.
            var run = 0;
            while (at - run >= 0 && json[at - run] == '\\')
            {
                run++;
            }

            if (run % 2 == 1)
            {
                clean.Append(json, from, at - from).Append("\\uFFFD");
                from = at + NulEscape.Length;
            }
        }

        return clean.Append(json, from, json.Length - from).ToString();
    }
}
