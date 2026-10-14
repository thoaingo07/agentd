using Agentd.Application.SystemLog;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>The daemon's log via <c>agentd.system_log_*</c> routines.</summary>
public sealed class SystemLogStore(NpgsqlDataSource dataSource) : ISystemLogStore
{
    public async Task<int> AddAsync(IReadOnlyList<NewSystemLogEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return 0;
        }

        await using var cmd = dataSource.CreateCommand("SELECT agentd.system_log_add($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.AddRange(new[]
        {
            P(entries.Select(e => e.At).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.TimestampTz),
            P(entries.Select(e => (short)e.Level).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Smallint),
            P(entries.Select(e => e.Category).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text),
            P(entries.Select(e => e.Message).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text),
            P(entries.Select(e => e.Exception).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text),
            P(entries.Select(e => e.JobId).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bigint),
        });
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<SystemLogEntry>> ListAsync(long? beforeId, LogLevel minLevel, string? category, string? search, int limit, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT id, at, level, category, message, exception, job_id FROM agentd.system_log_list($1, $2, $3, $4, $5)");
        cmd.Parameters.AddRange(new[]
        {
            P(beforeId, NpgsqlDbType.Bigint), P((short)minLevel, NpgsqlDbType.Smallint), P(category, NpgsqlDbType.Text), P(search, NpgsqlDbType.Text),
            P(limit, NpgsqlDbType.Integer),
        });
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<SystemLogEntry>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new SystemLogEntry(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), (LogLevel)r.GetInt16(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6)));
        }

        return rows;
    }

    public async Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.system_log_delete_before($1)");
        cmd.Parameters.Add(P(before, NpgsqlDbType.TimestampTz));
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => PgParameter.Of(value, type);
}
