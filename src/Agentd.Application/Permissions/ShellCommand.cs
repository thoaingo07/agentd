using System.Text;

namespace Agentd.Application.Permissions;

/// <summary>
/// Splits a shell command into the simple commands it runs, the way a POSIX shell reads it: separators (<c>;</c>,
/// <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>, <c>&amp;</c>, newlines, parentheses) count only outside quotes; commands inside
/// <c>$( … )</c> and backticks are extracted too (even inside double quotes, where the shell still runs them); here-document
/// bodies are data. When in doubt it splits more, which only asks more, never less.
/// </summary>
public static class ShellCommand
{
    public static IReadOnlyList<string> Split(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var parts = new List<string>();
        Walk(command, parts);
        return parts;
    }

    private static void Walk(string text, List<string> parts)
    {
        var current = new StringBuilder();
        var heredocs = new Queue<string>();
        var i = 0;
        void Flush()
        {
            var part = current.ToString().Trim();
            if (part.Length > 0)
            {
                parts.Add(part);
            }

            current.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                // A backslash-newline continues the line; any other escaped character is literal.
                if (text[i + 1] != '\n')
                {
                    current.Append(c).Append(text[i + 1]);
                }

                i += 2;
            }
            else if (c == '\'')
            {
                var end = text.IndexOf('\'', i + 1);
                end = end < 0 ? text.Length - 1 : end;
                current.Append(text, i, end - i + 1);
                i = end + 1;
            }
            else if (c == '"')
            {
                i = DoubleQuoted(text, i, current, parts);
            }
            else if (c == '`' || (c == '$' && i + 1 < text.Length && text[i + 1] == '('))
            {
                i = Substitution(text, i, parts);
                current.Append("$()");
            }
            else if (c == '<' && i + 1 < text.Length && text[i + 1] == '<' && (i + 2 >= text.Length || text[i + 2] != '<'))
            {
                i = HeredocStart(text, i, heredocs, current);
            }
            else if (c == '\n')
            {
                Flush();
                i++;
                while (heredocs.Count > 0)
                {
                    i = SkipHeredocBody(text, i, heredocs.Dequeue());
                }
            }
            else if (c is ';' or '(' or ')')
            {
                Flush();
                i++;
            }
            else if (c is '|' or '&')
            {
                // Redirections (2>&1, &>file, >&2) aren't separators.
                var previous = i > 0 ? text[i - 1] : ' ';
                var next = i + 1 < text.Length ? text[i + 1] : ' ';
                if (c == '&' && (previous is '>' or '<' || next == '>'))
                {
                    current.Append(c);
                    i++;
                }
                else
                {
                    Flush();
                    i += next == c ? 2 : 1;
                }
            }
            else
            {
                current.Append(c);
                i++;
            }
        }

        Flush();
    }

    /// <summary>A double-quoted string: literal text, but <c>$( … )</c> and backticks inside still run.</summary>
    private static int DoubleQuoted(string text, int start, StringBuilder current, List<string> parts)
    {
        current.Append('"');
        var i = start + 1;
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                current.Append(text, i, 2);
                i += 2;
            }
            else if (text[i] == '`' || (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '('))
            {
                i = Substitution(text, i, parts);
                current.Append("$()");
            }
            else
            {
                current.Append(text[i++]);
            }
        }

        current.Append('"');
        return Math.Min(i + 1, text.Length);
    }

    /// <summary>Extracts the commands of <c>$( … )</c> or <c>` … `</c> starting at <paramref name="start"/>; returns the index after it.</summary>
    private static int Substitution(string text, int start, List<string> parts)
    {
        int bodyStart, end;
        if (text[start] == '`')
        {
            bodyStart = start + 1;
            end = text.IndexOf('`', bodyStart);
            end = end < 0 ? text.Length : end;
        }
        else
        {
            bodyStart = start + 2;
            end = bodyStart;
            var depth = 1;
            char quote = '\0';
            for (; end < text.Length; end++)
            {
                var c = text[end];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                    else if (c == '\\' && quote == '"')
                    {
                        end++;
                    }
                }
                else if (c is '\'' or '"')
                {
                    quote = c;
                }
                else if (c == '\\')
                {
                    end++;
                }
                else if (c == '(')
                {
                    depth++;
                }
                else if (c == ')' && --depth == 0)
                {
                    break;
                }
            }
        }

        Walk(text[bodyStart..Math.Min(end, text.Length)], parts);
        return Math.Min(end + 1, text.Length);
    }

    /// <summary><c>&lt;&lt;WORD</c>, <c>&lt;&lt;-WORD</c>, <c>&lt;&lt;'WORD'</c>: remember WORD; the body starts at the next newline.</summary>
    private static int HeredocStart(string text, int start, Queue<string> heredocs, StringBuilder current)
    {
        var i = start + 2;
        if (i < text.Length && text[i] == '-')
        {
            i++;
        }

        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }

        var word = new StringBuilder();
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not (';' or '|' or '&' or ')' or '<' or '>'))
        {
            if (text[i] is not ('\'' or '"' or '\\'))
            {
                word.Append(text[i]);
            }

            i++;
        }

        if (word.Length > 0)
        {
            heredocs.Enqueue(word.ToString());
        }

        current.Append("<<").Append(word);
        return i;
    }

    private static int SkipHeredocBody(string text, int start, string delimiter)
    {
        var i = start;
        while (i < text.Length)
        {
            var end = text.IndexOf('\n', i);
            var line = end < 0 ? text[i..] : text[i..end];
            i = end < 0 ? text.Length : end + 1;
            if (line.Trim() == delimiter)
            {
                break;
            }
        }

        return i;
    }
}
