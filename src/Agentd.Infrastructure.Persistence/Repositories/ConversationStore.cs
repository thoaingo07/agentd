using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>Conversations via the <c>agentd.conversation_*</c> routines (one pooled connection per call).</summary>
public sealed class ConversationStore(NpgsqlDataSource dataSource) : IConversationStore
{
    public async Task<Result> AddAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var events = conversation.DequeueEvents();
        await using var cmd = dataSource.CreateCommand("SELECT agentd.conversation_insert($1, $2, $3, $4, $5, $6, $7)");
        cmd.Parameters.Add(P(conversation.JobId.Value, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(P(conversation.Provider.Value, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(conversation.ExternalConversationId, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(conversation.ExternalSpaceId, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(conversation.Link?.ToString(), NpgsqlDbType.Text));
        cmd.Parameters.Add(P(conversation.OpenedAt, NpgsqlDbType.TimestampTz));
        cmd.Parameters.Add(P(JobRows.EventsJson(events), NpgsqlDbType.Jsonb));
        try
        {
            conversation.Persisted(new ConversationId((long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!));
            return Result.Ok;
        }
        catch (PostgresException ex) when (SqlErrors.ToDomainError(ex) is { } error)
        {
            return error;
        }
    }

    public async Task<Result> SaveAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        await using var cmd = dataSource.CreateCommand("SELECT agentd.conversation_update($1, $2, $3)");
        cmd.Parameters.Add(P(conversation.Id.Value, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(P(conversation.StatusMessageId, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(conversation.ClosedAt, NpgsqlDbType.TimestampTz));
        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return Result.Ok;
        }
        catch (PostgresException ex) when (SqlErrors.ToDomainError(ex) is { } error)
        {
            return error;
        }
    }

    public Task<IReadOnlyList<Conversation>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken) =>
        ListAsync("SELECT * FROM agentd.conversation_list_by_job($1)", cancellationToken, P(jobId.Value, NpgsqlDbType.Bigint));

    public async Task<Conversation?> FindExternalAsync(ProviderKey provider, string externalConversationId, CancellationToken cancellationToken)
    {
        var found = await ListAsync("SELECT * FROM agentd.conversation_find_external($1, $2)", cancellationToken,
            P(provider.Value, NpgsqlDbType.Text), P(externalConversationId, NpgsqlDbType.Text)).ConfigureAwait(false);
        return found.Count > 0 ? found[0] : null;
    }

    public Task<IReadOnlyList<Conversation>> ListOpenByWorkItemAsync(WorkItemId workItem, CancellationToken cancellationToken) =>
        ListAsync("SELECT * FROM agentd.conversation_list_open_by_work_item($1)", cancellationToken, P(workItem.Value, NpgsqlDbType.Integer));

    public async Task<Result> MoveAsync(Conversation conversation, JobId jobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        await using var cmd = dataSource.CreateCommand("SELECT agentd.conversation_move($1, $2)");
        cmd.Parameters.Add(P(conversation.Id.Value, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(P(jobId.Value, NpgsqlDbType.Bigint));
        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            conversation.MovedTo(jobId);
            return Result.Ok;
        }
        catch (PostgresException ex) when (SqlErrors.ToDomainError(ex) is { } error)
        {
            return error;
        }
    }

    public Task<IReadOnlyList<Conversation>> ListOpenAsync(ProviderKey provider, CancellationToken cancellationToken) =>
        ListAsync("SELECT * FROM agentd.conversation_list_open($1)", cancellationToken, P(provider.Value, NpgsqlDbType.Text));

    private async Task<IReadOnlyList<Conversation>> ListAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<Conversation>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string? Str(string column) => r.IsDBNull(r.GetOrdinal(column)) ? null : r.GetString(r.GetOrdinal(column));
            var closed = r.GetOrdinal("closed_at");
            list.Add(Conversation.Rehydrate(new ConversationSnapshot(
                new ConversationId(r.GetInt64(r.GetOrdinal("id"))),
                new JobId(r.GetInt64(r.GetOrdinal("job_id"))),
                ProviderKey.From(r.GetString(r.GetOrdinal("provider"))),
                r.GetString(r.GetOrdinal("external_conversation_id")),
                Str("external_space_id"),
                Str("link") is { } link ? new Uri(link) : null,
                Str("status_message_id"),
                r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("opened_at")),
                r.IsDBNull(closed) ? null : r.GetFieldValue<DateTimeOffset>(closed))));
        }

        return list;
    }

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
