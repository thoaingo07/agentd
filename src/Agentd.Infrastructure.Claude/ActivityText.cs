using System.Text.Json;

namespace Agentd.Infrastructure.Claude;

/// <summary>A short, human description of a tool call for the live activity line ("📖 reading X", "🔧 dotnet test").</summary>
public static class ActivityText
{
    private const int MaxLength = 80;

    public static string Describe(string tool, string inputJson)
    {
        var input = Parse(inputJson);
        string? Arg(string name) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var text = tool switch
        {
            "Read" => $"📖 reading {FileName(Arg("file_path"))}",
            "Edit" or "MultiEdit" => $"✏️ editing {FileName(Arg("file_path"))}",
            "Write" => $"📝 writing {FileName(Arg("file_path"))}",
            "Grep" => $"🔎 searching for {Arg("pattern")}",
            "Glob" => $"🔎 finding {Arg("pattern")}",
            "Bash" => $"🔧 {Arg("description") ?? FirstLine(Arg("command"))}",
            "TodoWrite" => "🗒 updating its task list",
            _ when tool.StartsWith("mcp__agentd__", StringComparison.Ordinal) => $"💬 {tool["mcp__agentd__".Length..].Replace('_', ' ')}",
            _ => $"⚙️ {tool}",
        };
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }

    private static string FileName(string? path) => string.IsNullOrEmpty(path) ? "a file" : Path.GetFileName(path);

    private static string? FirstLine(string? text) => text?.Split('\n')[0];

    private static JsonElement Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
