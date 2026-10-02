using Agentd.Application.Messaging;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Messaging;

/// <summary>Outbox delivery via the <c>agentd.outbox_*</c> delivery routines.</summary>
public sealed class OutboxDelivery(NpgsqlDataSource dataSource) : IOutboxDelivery
{
    public async Task<IReadOnlyList<OutboxItem>> ClaimAsync(int limit, IReadOnlyList<ProviderKey> providers, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT * FROM agentd.outbox_claim($1, $2, $3)");
        cmd.Parameters.Add(P(limit, NpgsqlDbType.Integer));
        cmd.Parameters.Add(P(providers.Select(p => p.Value).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));
        cmd.Parameters.Add(P(DateTimeOffset.UtcNow, NpgsqlDbType.TimestampTz));
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<OutboxItem>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var (message, replace) = OutboxPayload.Read(r.GetString(r.GetOrdinal("payload")));
            var space = r.GetOrdinal("external_space_id");
            var status = r.GetOrdinal("status_message_id");
            items.Add(new OutboxItem(
                r.GetInt64(r.GetOrdinal("id")),
                new JobId(r.GetInt64(r.GetOrdinal("job_id"))),
                new ConversationRef(
                    ProviderKey.From(r.GetString(r.GetOrdinal("provider"))),
                    r.GetString(r.GetOrdinal("external_conversation_id")),
                    r.IsDBNull(space) ? null : r.GetString(space)),
                message,
                replace,
                r.IsDBNull(status) ? null : r.GetString(status),
                r.GetInt32(r.GetOrdinal("attempts"))));
        }

        return items;
    }

    public Task MarkSentAsync(long id, string externalMessageId, bool createdStatusMessage, CancellationToken cancellationToken) =>
        ExecAsync("SELECT agentd.outbox_mark_sent($1, $2, $3)", cancellationToken,
            P(id, NpgsqlDbType.Bigint), P(externalMessageId, NpgsqlDbType.Text), P(createdStatusMessage, NpgsqlDbType.Boolean));

    public async Task<bool> MarkRetryAsync(long id, string reason, DateTimeOffset nextAttemptAt, int maxAttempts, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.outbox_mark_retry($1, $2, $3, $4)");
        cmd.Parameters.Add(P(id, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(P(reason, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(nextAttemptAt, NpgsqlDbType.TimestampTz));
        cmd.Parameters.Add(P(maxAttempts, NpgsqlDbType.Integer));
        return (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) != "dead";
    }

    public Task MarkFailedAsync(long id, string reason, CancellationToken cancellationToken) =>
        ExecAsync("SELECT agentd.outbox_mark_failed($1, $2)", cancellationToken, P(id, NpgsqlDbType.Bigint), P(reason, NpgsqlDbType.Text));

    public async Task<int> ReleaseStaleAsync(DateTimeOffset claimedBefore, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.outbox_release_stale($1)");
        cmd.Parameters.Add(P(claimedBefore, NpgsqlDbType.TimestampTz));
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task ExecAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
