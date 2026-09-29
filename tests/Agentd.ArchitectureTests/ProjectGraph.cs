using System.Xml.Linq;

namespace Agentd.ArchitectureTests;

/// <summary>Declared dependencies of one project, read from its .csproj (not from compiled assemblies).</summary>
public sealed record ProjectInfo(
    string Name,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> FrameworkReferences)
{
    public static ProjectInfo Parse(string name, string csprojXml)
    {
        var doc = XDocument.Parse(csprojXml);
        IReadOnlyList<string> Items(string element, Func<string, string> map) =>
            doc.Descendants(element)
               .Select(e => (string?)e.Attribute("Include"))
               .Where(v => !string.IsNullOrWhiteSpace(v))
               .Select(v => map(v!))
               .ToList();

        return new ProjectInfo(
            name,
            Items("ProjectReference", v => Path.GetFileNameWithoutExtension(v.Replace('\\', '/'))),
            Items("PackageReference", v => v),
            Items("FrameworkReference", v => v));
    }
}

internal static class ProjectGraph
{
    /// <summary>All projects under src/, keyed by project name.</summary>
    public static IReadOnlyDictionary<string, ProjectInfo> Source { get; } = Load();

    private static Dictionary<string, ProjectInfo> Load() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot.Path, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(path => ProjectInfo.Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path)))
            .ToDictionary(p => p.Name, StringComparer.Ordinal);
}
