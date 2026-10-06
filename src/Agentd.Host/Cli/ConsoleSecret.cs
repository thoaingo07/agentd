using System.Text;

namespace Agentd.Host.Cli;

/// <summary>Reads a secret without it reaching the shell history, <c>ps</c> or the screen.</summary>
internal static class ConsoleSecret
{
    /// <summary>Piped stdin (<c>… | agentd secrets set X</c>, one trailing newline removed), or a no-echo prompt on the terminal.</summary>
    public static string? Read(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            var piped = Console.In.ReadToEnd();
            return piped.EndsWith("\r\n", StringComparison.Ordinal) ? piped[..^2] : piped.EndsWith('\n') ? piped[..^1] : piped;
        }

        Console.Error.Write(prompt);
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return value.ToString();
    }
}
