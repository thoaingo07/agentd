using Agentd.Application.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>Sessions per job and profile, and the submitted plan, via <c>agentd.job_session_*</c> / <c>agentd.job_plan_*</c>.</summary>
public sealed class JobSessionStore(NpgsqlDataSource dataSource) : IJobSessions, IJobPlans
{
    public async Task<(Guid Session, bool Created)> GetOrCreateAsync(JobId job, string profile, Guid proposed, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT session_id, created FROM agentd.job_session_get_or_create($1, $2, $3)");
        cmd.Parameters.AddRange(new[] { P(job.Value, NpgsqlDbType.Bigint), P(profile, NpgsqlDbType.Text), P(proposed, NpgsqlDbType.Uuid) });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetGuid(0), reader.GetBoolean(1));
    }

    public async Task SaveAsync(JobId job, string plan, DateTimeOffset submittedAt, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.job_plan_save($1, $2, $3)");
        cmd.Parameters.AddRange(new[] { P(job.Value, NpgsqlDbType.Bigint), P(plan, NpgsqlDbType.Text), P(submittedAt, NpgsqlDbType.TimestampTz) });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(JobId job, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.job_plan_get($1)");
        cmd.Parameters.Add(P(job.Value, NpgsqlDbType.Bigint));
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
