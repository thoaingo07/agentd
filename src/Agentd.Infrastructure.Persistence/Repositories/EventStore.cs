using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>Appends to the event log via <c>agentd.event_append</c>.</summary>
public sealed class EventStore(NpgsqlDataSource dataSource) : IEventStore
{
    public async Task<long> AppendAsync(JobId? jobId, string type, string payloadJson, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.event_append($1, $2, $3)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = jobId is { } id ? id.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = type, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = payloadJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
