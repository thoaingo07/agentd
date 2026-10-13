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

    public async Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.agent_prompt_delete_before($1)");
        cmd.Parameters.Add(P(before, NpgsqlDbType.TimestampTz));
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static NpgsqlParameter T(string? value) => P(value, NpgsqlDbType.Text);

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
