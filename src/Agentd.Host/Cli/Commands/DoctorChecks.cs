using System.Globalization;
using System.Text.RegularExpressions;

namespace Agentd.Host.Cli.Commands;

/// <summary>The pure parts of <c>agentd doctor</c>: version thresholds and which toolchains a repository needs.</summary>
internal static partial class DoctorChecks
{
    public static readonly Version MinimumGit = new(2, 40);
    public const int MinimumNode = 24;

    /// <summary>"git version 2.47.3" → 2.47.3; "v24.21.0" → 24.21.0; null when there's no version in it.</summary>
    public static Version? ParseVersion(string? output) =>
        VersionPattern().Match(output ?? string.Empty) is { Success: true } m && Version.TryParse(m.Value, out var v) ? v : null;

    /// <summary>A tool a repository needs, why (the file that says so) and how to check it.</summary>
    public sealed record Toolchain(string Tool, string Because, string[] VersionArgs, string Install);

    /// <summary>Which toolchains the files of a repository's base branch call for (until the kit's <c>verify</c> section exists).</summary>
    public static IReadOnlyList<Toolchain> Toolchains(IReadOnlyCollection<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        // Shallowest first, so "because" names the repo's main build file, not one deep in a sub-project.
        var names = files.Select(f => f.Replace('\\', '/')).OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.Ordinal).ToList();
        string? First(params string[] patterns) => names.FirstOrDefault(f => patterns.Any(p => Matches(f, p)));
        var needs = new List<Toolchain>();
        void Need(string tool, string? because, string[] args, string install)
        {
            if (because is not null && needs.All(n => n.Tool != tool))
            {
                needs.Add(new(tool, because, args, install));
            }
        }

        Need("dotnet", First("*.sln", "*.slnx", "*.csproj", "*.fsproj", "global.json"), ["--version"], "install the .NET SDK (https://dot.net), e.g. sudo apt install dotnet-sdk-10.0");
        var package = First("package.json");
        Need("node", package, ["--version"], "install Node.js 24");
        Need(names.Any(f => Matches(f, "pnpm-lock.yaml")) ? "pnpm" : names.Any(f => Matches(f, "yarn.lock")) ? "yarn" : "npm", package, ["--version"], "comes with Node.js (corepack enable for pnpm/yarn)");
        Need("mvn", First("pom.xml"), ["--version"], "install Maven, e.g. sudo apt install maven");
        Need("java", First("pom.xml", "build.gradle", "build.gradle.kts"), ["-version"], "install a JDK, e.g. sudo apt install openjdk-21-jdk");
        Need("go", First("go.mod"), ["version"], "install Go (https://go.dev/dl)");
        Need("cargo", First("Cargo.toml"), ["--version"], "install Rust (https://rustup.rs)");
        Need("python3", First("pyproject.toml", "requirements.txt", "setup.py"), ["--version"], "install Python 3, e.g. sudo apt install python3");
        Need("helm", First("Chart.yaml"), ["version", "--short"], "install Helm (https://helm.sh/docs/intro/install)");
        Need("docker", First("Dockerfile", "*.Dockerfile", "docker-compose.yml", "compose.yaml"), ["--version"], "install Docker, and add agentd's user to the docker group");
        Need("task", First("Taskfile.yml", "Taskfile.yaml"), ["--version"], "install Task (https://taskfile.dev/installation)");
        Need("make", First("Makefile"), ["--version"], "install make, e.g. sudo apt install make");
        return needs;
    }

    /// <summary>A file name pattern (<c>*.csproj</c> or an exact name) matched against the last path segment.</summary>
    private static bool Matches(string path, string pattern)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return pattern.StartsWith("*.", StringComparison.Ordinal)
            ? name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?")]
    private static partial Regex VersionPattern();

    public static string Minimum(Version v) => v.ToString(2).ToString(CultureInfo.InvariantCulture);
}
