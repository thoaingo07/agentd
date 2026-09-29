using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Agentd.Infrastructure.Persistence.Migrations;

/// <summary>A SQL script embedded in this assembly, identified by its path under <c>Database/</c>.</summary>
internal sealed record SqlScript(string Id, string Sql, string Checksum)
{
    private const string MigrationsPrefix = "Migrations/";
    private const string RoutinesPrefix = "Routines/";

    public static IReadOnlyList<SqlScript> LoadMigrations() => Load(MigrationsPrefix);

    public static IReadOnlyList<SqlScript> LoadRoutines() => Load(RoutinesPrefix);

    public static SqlScript Create(string id, string sql)
    {
        var normalized = sql.ReplaceLineEndings("\n");
        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return new SqlScript(id, normalized, checksum);
    }

    private static List<SqlScript> Load(string prefix)
    {
        var assembly = typeof(SqlScript).Assembly;
        return assembly.GetManifestResourceNames()
            .Select(name => (Name: name, Id: name.Replace('\\', '/')))
            .Where(r => r.Id.StartsWith(prefix, StringComparison.Ordinal) && r.Id.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => Create(r.Id, Read(assembly, r.Name)))
            .ToList();
    }

    private static string Read(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded SQL resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
