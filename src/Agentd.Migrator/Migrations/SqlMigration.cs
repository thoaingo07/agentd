using System.Reflection;
using FluentMigrator;

namespace Agentd.Migrator.Migrations;

/// <summary>
/// A versioned migration written in raw SQL. The script is found by the version number in the
/// <see cref="MigrationAttribute"/>: <c>Migrations/{version}_{name}.up.sql</c> (required) and
/// <c>Migrations/{version}_{name}.down.sql</c> (optional). Subclasses are one line:
/// <code>[Migration(2026_09_29_0001, "Initial schema")] public sealed class InitialSchema : SqlMigration;</code>
/// Applied scripts are immutable; add a new migration instead of editing one.
/// </summary>
public abstract class SqlMigration : Migration
{
    public override void Up() => Execute.Sql(SqlResources.Read(FindScript("up")
        ?? throw new InvalidOperationException($"{GetType().Name}: missing Migrations/{Version}_*.up.sql")));

    public override void Down() => Execute.Sql(SqlResources.Read(FindScript("down")
        ?? throw new InvalidOperationException($"{GetType().Name} has no down script (Migrations/{Version}_*.down.sql).")));

    private long Version => GetType().GetCustomAttribute<MigrationAttribute>()?.Version
        ?? throw new InvalidOperationException($"{GetType().Name} is missing [Migration(version)].");

    private string? FindScript(string direction)
    {
        var prefix = $"Migrations/{Version}_";
        var matches = SqlResources.List("Migrations")
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && p.EndsWith($".{direction}.sql", StringComparison.Ordinal))
            .ToList();
        return matches.Count <= 1
            ? matches.SingleOrDefault()
            : throw new InvalidOperationException($"More than one {direction} script for migration {Version}: {string.Join(", ", matches)}");
    }
}
