using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Infrastructure.Persistence.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>
/// Jobs via the <c>agentd.job_*</c> routines. Each call opens its own pooled connection
/// (thread-safe; see data-access.md). State and domain events are written in one routine call.
/// </summary>
public sealed class JobRepository(NpgsqlDataSource dataSource, IClock clock) : IJobRepository
{
    public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken) =>
        SingleAsync("SELECT * FROM agentd.job_get($1)", cancellationToken, P(id.Value, NpgsqlDbType.Bigint));

    public Task<Job?> FindActiveByWorkItemAsync(WorkItemId workItem, CancellationToken cancellationToken) =>
        SingleAsync("SELECT * FROM agentd.job_find_active_by_work_item($1)", cancellationToken, P(workItem.Value, NpgsqlDbType.Integer));

    public async Task<Result> AddAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var events = job.DequeueEvents();
        await using var cmd = dataSource.CreateCommand("SELECT id, version FROM agentd.job_insert($1, $2, $3, $4, $5, $6, $7)");
        cmd.Parameters.Add(P(job.WorkItemId.Value, NpgsqlDbType.Integer));
        cmd.Parameters.Add(P(job.Repository.Value, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(job.Title, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(job.State.ToString(), NpgsqlDbType.Text));
        cmd.Parameters.Add(P(job.Attempt, NpgsqlDbType.Integer));
        cmd.Parameters.Add(P(job.CreatedAt, NpgsqlDbType.TimestampTz));
        cmd.Parameters.Add(P(JobRows.EventsJson(events), NpgsqlDbType.Jsonb));
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            job.Persisted(new JobId(reader.GetInt64(0)), reader.GetInt64(1));
            return Result.Ok;
        }
        catch (PostgresException ex) when (SqlErrors.ToDomainError(ex) is { } error)
        {
            return error;
        }
    }

    /// <summary>
    /// Saves the job and appends its events; the messages those events post (<see cref="JobEventMessages"/>)
    /// are written to the outbox in the same batch, i.e. the same implicit transaction.
    /// </summary>
    public async Task<Result> SaveAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var events = job.DequeueEvents();
        await using var batch = dataSource.CreateBatch();
        var save = new NpgsqlBatchCommand(
            "SELECT agentd.job_save($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21, $22)");
        save.Parameters.Add(P(job.Id.Value, NpgsqlDbType.Bigint));
        save.Parameters.Add(P(job.Version, NpgsqlDbType.Bigint));
        save.Parameters.Add(P(job.State.ToString(), NpgsqlDbType.Text));
        save.Parameters.Add(P(job.Branch?.Value, NpgsqlDbType.Text));
        save.Parameters.Add(P(job.Worktree?.Value, NpgsqlDbType.Text));
        save.Parameters.Add(P(job.Session?.Value, NpgsqlDbType.Uuid));
        save.Parameters.Add(P(job.Attempt, NpgsqlDbType.Integer));
        save.Parameters.Add(P(job.ResumeCount, NpgsqlDbType.Integer));
        save.Parameters.Add(P(job.PublishAttempts, NpgsqlDbType.Integer));
        save.Parameters.Add(P(job.LastError, NpgsqlDbType.Text));
        save.Parameters.Add(P(job.NotBefore, NpgsqlDbType.TimestampTz));
        save.Parameters.Add(P(job.Draft?.Title, NpgsqlDbType.Text));
        save.Parameters.Add(P(job.Draft?.Description, NpgsqlDbType.Text));
        save.Parameters.Add(P(job.Draft?.Summary, NpgsqlDbType.Text));
        save.Parameters.Add(P(job.PullRequest?.Value.ToString(), NpgsqlDbType.Text));
        save.Parameters.Add(P(job.UpdatedAt, NpgsqlDbType.TimestampTz));
        save.Parameters.Add(P(JobRows.EventsJson(events), NpgsqlDbType.Jsonb));
        save.Parameters.Add(P(job.PendingMessages.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));
        save.Parameters.Add(P(job.WaitingSince, NpgsqlDbType.TimestampTz));
        save.Parameters.Add(P(job.WaitReminders, NpgsqlDbType.Integer));
        save.Parameters.Add(P(job.PlanStatus.ToString(), NpgsqlDbType.Text));
        save.Parameters.Add(P(JobRows.EstimateJson(job.Estimate), NpgsqlDbType.Jsonb));
        batch.BatchCommands.Add(save);

        var outbox = JobEventMessages.For(events);
        if (outbox.Count > 0)
        {
            var enqueue = new NpgsqlBatchCommand("SELECT agentd.outbox_enqueue($1, $2)");
            enqueue.Parameters.Add(P(job.Id.Value, NpgsqlDbType.Bigint));
            enqueue.Parameters.Add(P(OutboxPayload.EnqueueJson(outbox), NpgsqlDbType.Jsonb));
            batch.BatchCommands.Add(enqueue);
        }

        try
        {
            var version = (long)(await batch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            job.Persisted(job.Id, version);
            return Result.Ok;
        }
        catch (PostgresException ex) when (SqlErrors.ToDomainError(ex) is { } error)
        {
            return error;
        }
    }

    public Task<Job?> DequeueNextAsync(string worker, CancellationToken cancellationToken) =>
        SingleAsync("SELECT * FROM agentd.job_dequeue($1, $2)", cancellationToken,
            P(worker, NpgsqlDbType.Text), P(clock.UtcNow, NpgsqlDbType.TimestampTz));

    public Task<IReadOnlyList<Job>> ListByStateAsync(IReadOnlyCollection<JobState> states, CancellationToken cancellationToken) =>
        ListAsync("SELECT * FROM agentd.job_list_by_state($1)", cancellationToken,
            P(states.Select(s => s.ToString()).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));

    public Task<IReadOnlyList<Job>> ListRecentAsync(TimeSpan window, CancellationToken cancellationToken) =>
        ListAsync("SELECT * FROM agentd.job_list_recent($1)", cancellationToken, P(clock.UtcNow - window, NpgsqlDbType.TimestampTz));

    private async Task<Job?> SingleAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        var jobs = await ListAsync(sql, ct, parameters).ConfigureAwait(false);
        return jobs.Count > 0 ? jobs[0] : null;
    }

    private async Task<IReadOnlyList<Job>> ListAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return (await JobRows.ReadAllAsync(reader, ct).ConfigureAwait(false)).Select(s => Job.Rehydrate(s, clock)).ToList();
    }

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
