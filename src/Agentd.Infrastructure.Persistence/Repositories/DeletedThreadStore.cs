using Agentd.Application.Ports;
using Agentd.Domain.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>Deleted chat threads via <c>agentd.thread_deleted</c> (one pooled connection per call).</summary>
public sealed class DeletedThreadStore(NpgsqlDataSource dataSource) : IDeletedThreads
{
    public async Task<int> MarkDeletedAsync(ProviderKey provider, string threadId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.thread_deleted($1, $2, $3)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = provider.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = threadId, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = at, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
