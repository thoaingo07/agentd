using Agentd.Application.Prompts;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>The prompt log via <c>agentd.agent_prompt_*</c> routines (docs/architect/prompt-log.md).</summary>
public sealed class PromptLogStore(NpgsqlDataSource dataSource) : IPromptLog
{
    public async Task<long> StartAsync(PromptStart prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        await using var cmd = dataSource.CreateCommand("SELECT agentd.agent_prompt_start($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)");
        cmd.Parameters.AddRange(new[]
        {
            T(prompt.Source), P(prompt.SourceId, NpgsqlDbType.Bigint), T(prompt.Step), P(prompt.Session, NpgsqlDbType.Uuid), P(prompt.Resume, NpgsqlDbType.Boolean),
            T(prompt.Model), T(prompt.Effort), T(prompt.Profile), T(prompt.Rules), T(prompt.Prompt),
        });
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task FinishAsync(long id, string outcome, string? failure, string? reply, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.agent_prompt_finish($1, $2, $3, $4)");
        cmd.Parameters.AddRange(new[] { P(id, NpgsqlDbType.Bigint), T(outcome), T(failure), T(reply) });
        await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PromptSummary>> ListAsync(PromptFilter filter, long? before, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var cmd = dataSource.CreateCommand("SELECT * FROM agentd.agent_prompt_list($1, $2, $3, $4, $5, $6, $7, $8)");
        cmd.Parameters.AddRange(new[]
        {
            T(filter.Source), P(filter.SourceId, NpgsqlDbType.Bigint), T(filter.Step), T(filter.Model), T(filter.Outcome),
            P(filter.From, NpgsqlDbType.TimestampTz), P(before, NpgsqlDbType.Bigint), P(limit, NpgsqlDbType.Integer),
        });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<PromptSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PromptSummary(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), S(reader, 3), G(reader, 4), reader.GetBoolean(5),
                S(reader, 6), S(reader, 7), S(reader, 8), S(reader, 9), S(reader, 10), reader.GetFieldValue<DateTimeOffset>(11), D(reader, 12),
                reader.GetString(13), reader.GetInt32(14), reader.GetInt32(15), reader.GetBoolean(16)));
        }

        return rows;
    }

    public async Task<PromptDetail?> GetAsync(long id, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT * FROM agentd.agent_prompt_get($1)");
        cmd.Parameters.Add(P(id, NpgsqlDbType.Bigint));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PromptDetail(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), S(reader, 3), G(reader, 4), reader.GetBoolean(5),
                S(reader, 6), S(reader, 7), S(reader, 8), S(reader, 9), S(reader, 10), reader.GetFieldValue<DateTimeOffset>(11), D(reader, 12),
                reader.GetString(13), S(reader, 14), S(reader, 15))
            : null;
    }

    public async Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.agent_prompt_delete_before($1)");
        cmd.Parameters.Add(P(before, NpgsqlDbType.TimestampTz));
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static Guid? G(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    private static DateTimeOffset? D(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    private static NpgsqlParameter T(string? value) => P(value, NpgsqlDbType.Text);

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
