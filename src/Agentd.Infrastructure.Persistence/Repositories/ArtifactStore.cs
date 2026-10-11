using Agentd.Application.Artifacts;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>Artifacts via <c>agentd.artifact_*</c> routines.</summary>
public sealed class ArtifactStore(NpgsqlDataSource dataSource) : IArtifactStore
{
    public async Task<PublishedArtifact> PublishAsync(string name, string title, string html, string source, long sourceId, string author, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT name, created FROM agentd.artifact_publish($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.AddRange(new[] { T(name), T(title), T(html), T(source), P(sourceId, NpgsqlDbType.Bigint), T(author) });
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await r.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new PublishedArtifact(r.GetString(0), r.GetBoolean(1));
    }

    public async Task<Artifact?> GetAsync(string name, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT name, title, html, source, source_id, author, updated_at, expired_at FROM agentd.artifact_get($1)");
        cmd.Parameters.Add(T(name));
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await r.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new Artifact(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6),
                r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7))
            : null;
    }

    public async Task<IReadOnlyList<ArtifactSummary>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT name, title, source, source_id, author, created_at, updated_at FROM agentd.artifact_list($1)");
        cmd.Parameters.Add(P(limit, NpgsqlDbType.Integer));
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<ArtifactSummary>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ArtifactSummary(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5), r.GetFieldValue<DateTimeOffset>(6)));
        }

        return rows;
    }

    public async Task<int> ExpireAsync(DateTimeOffset updatedBefore, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.artifact_expire($1)");
        cmd.Parameters.Add(P(updatedBefore, NpgsqlDbType.TimestampTz));
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static NpgsqlParameter T(string? value) => P(value, NpgsqlDbType.Text);

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
