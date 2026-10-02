using Agentd.Application.Messaging;
using Agentd.Domain.Jobs.ValueObjects;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Messaging;

/// <summary>Standalone outbox writes (progress, mirrored replies) via <c>agentd.outbox_enqueue</c>.</summary>
public sealed class Outbox(NpgsqlDataSource dataSource) : IOutbox
{
    public async Task EnqueueAsync(JobId jobId, IReadOnlyList<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.outbox_enqueue($1, $2)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = jobId.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = OutboxPayload.EnqueueJson(messages), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
