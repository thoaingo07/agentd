using System.Text.Json;
using Agentd.Application.Incidents;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>Incidents and each app's read position via <c>agentd.incident_*</c> routines.</summary>
public sealed class IncidentStore(NpgsqlDataSource dataSource) : IIncidentStore
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public async Task<IncidentRecorded?> RecordAsync(string app, ErrorGroup group, int minCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        await using var cmd = dataSource.CreateCommand("SELECT id, created, reopened FROM agentd.incident_record($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)");
        cmd.Parameters.AddRange(new[]
        {
            T(app), T(group.Fingerprint), T(group.Kind), T(group.Title), T(group.Message), T(group.Operation),
            P(JsonSerializer.Serialize(group.Sample, s_json), NpgsqlDbType.Jsonb), P(group.Count, NpgsqlDbType.Bigint),
            P(group.FirstSeen, NpgsqlDbType.TimestampTz), P(group.LastSeen, NpgsqlDbType.TimestampTz), P(minCount, NpgsqlDbType.Integer),
        });
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await r.ReadAsync(cancellationToken).ConfigureAwait(false) ? new IncidentRecorded(r.GetInt64(0), r.GetBoolean(1), r.GetBoolean(2)) : null;
    }

    public async Task<Incident?> GetAsync(long id, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.incident_get($1)", cancellationToken, P(id, NpgsqlDbType.Bigint)).ConfigureAwait(false)).SingleOrDefault();

    public Task<IReadOnlyList<Incident>> ListAsync(string? status, int limit, CancellationToken cancellationToken) =>
        ReadAsync("SELECT * FROM agentd.incident_list($1, $2)", cancellationToken, T(status), P(limit, NpgsqlDbType.Integer));

    public async Task SetThreadAsync(long id, string provider, string threadId, string? spaceId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.incident_set_thread($1, $2, $3, $4)");
        cmd.Parameters.AddRange(new[] { P(id, NpgsqlDbType.Bigint), T(provider), T(threadId), T(spaceId) });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetStatusAsync(long id, string status, string? from, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.incident_set_status($1, $2, $3)");
        cmd.Parameters.AddRange(new[] { P(id, NpgsqlDbType.Bigint), T(status), T(from) });
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task SetAnalysisAsync(long id, string analysis, Guid session, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.incident_set_analysis($1, $2, $3)");
        cmd.Parameters.AddRange(new[] { P(id, NpgsqlDbType.Bigint), T(analysis), P(session, NpgsqlDbType.Uuid) });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int?> SetWorkItemAsync(long id, int workItemId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.incident_set_work_item($1, $2)");
        cmd.Parameters.AddRange(new[] { P(id, NpgsqlDbType.Bigint), P(workItemId, NpgsqlDbType.Integer) });
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int kept ? kept : null;
    }

    public async Task<Incident?> FindByThreadAsync(string provider, string threadId, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.incident_find_by_thread($1, $2)", cancellationToken, T(provider), T(threadId)).ConfigureAwait(false)).SingleOrDefault();

    public async Task<DateTimeOffset?> GetCursorAsync(string app, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.incident_cursor_get($1)");
        cmd.Parameters.Add(T(app));
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is DateTime at ? new DateTimeOffset(at, TimeSpan.Zero) : null;
    }

    public async Task SetCursorAsync(string app, DateTimeOffset checkedUntil, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.incident_cursor_set($1, $2)");
        cmd.Parameters.AddRange(new[] { T(app), P(checkedUntil, NpgsqlDbType.TimestampTz) });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Incident>> ReadAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<Incident>();
        int O(string column) => r.GetOrdinal(column);
        string? S(string column) => r.IsDBNull(O(column)) ? null : r.GetString(O(column));
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new Incident(
                r.GetInt64(O("id")), r.GetString(O("app")), r.GetString(O("fingerprint")), r.GetString(O("kind")), r.GetString(O("title")), S("message"), S("operation"),
                JsonSerializer.Deserialize<ErrorSample>(r.GetString(O("sample")), s_json) ?? new ErrorSample(null, null, null),
                r.GetString(O("status")), r.GetInt64(O("total_count")), r.GetFieldValue<DateTimeOffset>(O("first_seen")), r.GetFieldValue<DateTimeOffset>(O("last_seen")),
                S("provider"), S("thread_id"), S("space_id"), r.IsDBNull(O("work_item_id")) ? null : r.GetInt32(O("work_item_id")),
                r.GetFieldValue<DateTimeOffset>(O("created_at")), S("analysis"), r.IsDBNull(O("session")) ? null : r.GetGuid(O("session")),
                r.IsDBNull(O("analyzed_at")) ? null : r.GetFieldValue<DateTimeOffset>(O("analyzed_at"))));
        }

        return rows;
    }

    private static NpgsqlParameter T(string? value) => P(value, NpgsqlDbType.Text);

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => PgParameter.Of(value, type);
}
