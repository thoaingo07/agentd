namespace Agentd.ArchitectureTests;

/// <summary>
/// One Clean Architecture rule: projects matching <see cref="Applies"/> must not reference
/// any project or package/framework whose name starts with one of the forbidden prefixes.
/// </summary>
public sealed record DependencyRule(
    string Description,
    Func<string, bool> Applies,
    string[] ForbiddenProjects,
    string[] ForbiddenPackages)
{
    public override string ToString() => Description;

    public IEnumerable<string> Violations(ProjectInfo project)
    {
        if (!Applies(project.Name))
        {
            yield break;
        }

        foreach (var reference in project.ProjectReferences.Where(r => Matches(r, ForbiddenProjects)))
        {
            yield return $"{project.Name} → {reference} violates \"{Description}\"";
        }

        foreach (var reference in project.PackageReferences.Concat(project.FrameworkReferences).Where(r => Matches(r, ForbiddenPackages)))
        {
            yield return $"{project.Name} → {reference} violates \"{Description}\"";
        }
    }

    private static bool Matches(string name, string[] prefixes) =>
        prefixes.Any(p => name.Equals(p, StringComparison.Ordinal) || name.StartsWith(p + ".", StringComparison.Ordinal) || (p.EndsWith('*') && name.StartsWith(p.TrimEnd('*'), StringComparison.Ordinal)));
}

internal static class DependencyRules
{
    private const string Domain = "Agentd.Domain";
    private const string Application = "Agentd.Application";
    private const string Infrastructure = "Agentd.Infrastructure.*";

    public static IReadOnlyList<DependencyRule> All { get; } =
    [
        new("Domain depends on nothing",
            n => n == Domain,
            ["Agentd.*"],
            ["Microsoft.AspNetCore.App", "Microsoft.Extensions.*", "Npgsql", "Dapper", "Microsoft.Agents.AI"]),

        new("Application is framework-free",
            n => n == Application,
            [Infrastructure, "Agentd.Bff", "Agentd.Mcp", "Agentd.Host", "Agentd.ServiceDefaults"],
            ["Microsoft.AspNetCore.App", "Npgsql", "Dapper", "Discord", "Telegram", "Microsoft.Agents.AI", "Aspire.*"]),

        new("Infrastructure depends only on Application and Domain",
            n => n.StartsWith("Agentd.Infrastructure.", StringComparison.Ordinal),
            ["Agentd.Bff", "Agentd.Mcp", "Agentd.Host", "Agentd.ServiceDefaults"],
            []),

        new("Presentation (Bff, Mcp) never references Infrastructure",
            n => n is "Agentd.Bff" or "Agentd.Mcp",
            [Infrastructure, "Agentd.Host", "Agentd.ServiceDefaults"],
            ["Npgsql", "Dapper"]),

        new("Only Host references ServiceDefaults",
            n => n != "Agentd.Host",
            ["Agentd.ServiceDefaults"],
            []),

        new("No ORM anywhere (PostgreSQL functions via Npgsql; see docs/architect/data-access.md)",
            _ => true,
            [],
            ["Microsoft.EntityFrameworkCore*", "Npgsql.EntityFrameworkCore*", "Aspire.Npgsql.EntityFrameworkCore*"]),
    ];

    /// <summary>Infrastructure projects must not reference each other (checked separately: needs the project's own name).</summary>
    public static IEnumerable<string> CrossInfrastructureViolations(ProjectInfo project) =>
        project.Name.StartsWith("Agentd.Infrastructure.", StringComparison.Ordinal)
            ? project.ProjectReferences
                .Where(r => r.StartsWith("Agentd.Infrastructure.", StringComparison.Ordinal) && r != project.Name)
                .Select(r => $"{project.Name} → {r} violates \"Infrastructure projects are independent\"")
            : [];
}
