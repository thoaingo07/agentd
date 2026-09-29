using System.Reflection;
using System.Text;

namespace Agentd.Migrator;

/// <summary>Reads the SQL scripts embedded in this assembly (logical names are paths such as <c>Migrations/…sql</c>).</summary>
internal static class SqlResources
{
    private static readonly Assembly s_assembly = typeof(SqlResources).Assembly;

    /// <summary>Paths of all embedded scripts under <paramref name="folder"/>, in ordinal order.</summary>
    public static IReadOnlyList<string> List(string folder) =>
        s_assembly.GetManifestResourceNames()
            .Select(n => n.Replace('\\', '/'))
            .Where(n => n.StartsWith(folder + "/", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    public static string Read(string path)
    {
        var name = s_assembly.GetManifestResourceNames().SingleOrDefault(n => n.Replace('\\', '/') == path)
            ?? throw new InvalidOperationException($"Embedded SQL script '{path}' not found.");
        using var stream = s_assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
