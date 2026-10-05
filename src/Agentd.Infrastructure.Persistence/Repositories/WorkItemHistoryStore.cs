using Agentd.Application.Events;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>A work item's history via <c>agentd.*_by_work_item</c> routines.</summary>
public sealed class WorkItemHistoryStore(NpgsqlDataSource dataSource, IClock clock) : IWorkItemHistory
{
    public async Task<IReadOnlyList<Job>> ListJobsAsync(WorkItemId workItem, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT * FROM agentd.job_list_by_work_item($1)");
        cmd.Parameters.Add(P(workItem.Value, NpgsqlDbType.Integer));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return (await JobRows.ReadAllAsync(reader, cancellationToken).ConfigureAwait(false)).Select(s => Job.Rehydrate(s, clock)).ToList();
    }

    public async Task<IReadOnlyList<AgentEventDto>> ReadEventsAsync(WorkItemId workItem, long? after, long? before, int limit, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT * FROM agentd.event_list_by_work_item($1, $2, $3, $4)");
        cmd.Parameters.AddRange(new[] { P(workItem.Value, NpgsqlDbType.Integer), P(after, NpgsqlDbType.Bigint), P(before, NpgsqlDbType.Bigint), P(limit, NpgsqlDbType.Integer) });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var events = await EventStore.ReadRowsAsync(reader, cancellationToken).ConfigureAwait(false);
        return before is null ? events : events.Reverse().ToList();
    }

    public async Task<IReadOnlyList<PostedMessage>> ListPostedAsync(WorkItemId workItem, int limit, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT * FROM agentd.outbox_list_by_work_item($1, $2)");
        cmd.Parameters.AddRange(new[] { P(workItem.Value, NpgsqlDbType.Integer), P(limit, NpgsqlDbType.Integer) });
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var messages = new List<PostedMessage>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new PostedMessage(
                r.GetInt64(0),
                r.IsDBNull(1) ? null : r.GetInt64(1),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.IsDBNull(5) ? string.Empty : r.GetString(5),
                r.GetFieldValue<DateTimeOffset>(6),
                r.IsDBNull(7) ? null : r.GetString(7)));
        }

        return messages;
    }

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
