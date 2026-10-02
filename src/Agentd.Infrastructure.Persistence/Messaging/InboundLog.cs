using Agentd.Application.Messaging;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Domain.Users;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Messaging;

/// <summary>Inbound idempotency via the <c>agentd.inbound_*</c> routines.</summary>
public sealed class InboundLog(NpgsqlDataSource dataSource) : IInboundLog
{
    public async Task<bool> TryRecordAsync(ProviderKey provider, string externalMessageId, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.inbound_try_record($1, $2, $3)");
        cmd.Parameters.Add(P(provider.Value, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(externalMessageId, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(receivedAt, NpgsqlDbType.TimestampTz));
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task SetOutcomeAsync(ProviderKey provider, string externalMessageId, string outcome, JobId? jobId, UserId? userId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.inbound_set_outcome($1, $2, $3, $4, $5)");
        cmd.Parameters.Add(P(provider.Value, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(externalMessageId, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(outcome, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(jobId?.Value, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(P(userId?.Value, NpgsqlDbType.Bigint));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ForgetAsync(ProviderKey provider, string externalMessageId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.inbound_forget($1, $2)");
        cmd.Parameters.Add(P(provider.Value, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(externalMessageId, NpgsqlDbType.Text));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
