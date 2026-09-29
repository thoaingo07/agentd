namespace Agentd.ArchitectureTests;

/// <summary>Locates the repository root (the directory containing Agentd.slnx).</summary>
internal static class RepoRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Agentd.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Agentd.slnx not found above " + AppContext.BaseDirectory);
    }
}
