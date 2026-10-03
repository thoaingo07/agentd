using System.Text.Json;
using Agentd.Application.Events;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>The event log via <c>agentd.event_*</c> routines. Payloads are redacted before they are stored.</summary>
public sealed class EventStore(NpgsqlDataSource dataSource) : IEventStore, IEventReader
{
    public async Task<long> AppendAsync(JobId? jobId, string type, string payloadJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        await using var cmd = dataSource.CreateCommand("SELECT agentd.event_append($1, $2, $3)");
        cmd.Parameters.Add(P(jobId?.Value, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(P(type, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(SecretRedactor.Redact(payloadJson), NpgsqlDbType.Jsonb));
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public Task<IReadOnlyList<AgentEventDto>> ReadAfterAsync(JobId? jobId, long afterSeq, int limit, CancellationToken cancellationToken) =>
        ReadAsync("SELECT * FROM agentd.event_read_after($1, $2, $3)", cancellationToken,
            P(jobId?.Value, NpgsqlDbType.Bigint), P(afterSeq, NpgsqlDbType.Bigint), P(limit, NpgsqlDbType.Integer));

    public async Task<IReadOnlyList<AgentEventDto>> ReadBeforeAsync(JobId jobId, long beforeSeq, int limit, CancellationToken cancellationToken)
    {
        var newestFirst = await ReadAsync("SELECT * FROM agentd.event_read_before($1, $2, $3)", cancellationToken,
            P(jobId.Value, NpgsqlDbType.Bigint), P(beforeSeq, NpgsqlDbType.Bigint), P(limit, NpgsqlDbType.Integer)).ConfigureAwait(false);
        return newestFirst.Reverse().ToList();
    }

    public async Task<AgentEventDto?> GetAsync(long seq, CancellationToken cancellationToken)
    {
        var found = await ReadAsync("SELECT * FROM agentd.event_get($1)", cancellationToken, P(seq, NpgsqlDbType.Bigint)).ConfigureAwait(false);
        return found.Count > 0 ? found[0] : null;
    }

    private async Task<IReadOnlyList<AgentEventDto>> ReadAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var events = new List<AgentEventDto>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var job = r.GetOrdinal("job_id");
            using var payload = JsonDocument.Parse(r.GetString(r.GetOrdinal("payload")));
            events.Add(new AgentEventDto(
                r.GetInt64(r.GetOrdinal("seq")),
                r.IsDBNull(job) ? null : r.GetInt64(job),
                r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("ts")),
                r.GetString(r.GetOrdinal("type")),
                payload.RootElement.Clone()));
        }

        return events;
    }

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
