using System.Text.Json;
using Agentd.Application.Monitor;
using Agentd.Domain.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>The PR Monitor's watch list via <c>agentd.pr_watch_*</c> routines.</summary>
public sealed class PrWatchStore(NpgsqlDataSource dataSource) : IPrWatchStore
{
    public async Task<(long Id, bool Created)> InsertAsync(string repository, int pullRequestId, string title, string watchedBy, ProviderKey provider, string threadId, string? spaceId,
        IReadOnlyList<int> seenComments, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT id, created FROM agentd.pr_watch_insert($1, $2, $3, $4, $5, $6, $7, $8)");
        cmd.Parameters.AddRange(new[] { T(repository), P(pullRequestId, NpgsqlDbType.Integer), T(title), T(watchedBy), T(provider.Value), T(threadId), T(spaceId),
            P(seenComments.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Integer) });
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await r.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (r.GetInt64(0), r.GetBoolean(1));
    }

    public async Task<PrWatch?> GetAsync(long id, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.pr_watch_get($1)", cancellationToken, P(id, NpgsqlDbType.Bigint)).ConfigureAwait(false)).SingleOrDefault();

    public async Task<PrWatch?> FindActiveAsync(string repository, int pullRequestId, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.pr_watch_find_active($1, $2)", cancellationToken, T(repository), P(pullRequestId, NpgsqlDbType.Integer)).ConfigureAwait(false)).SingleOrDefault();

    public async Task<PrWatch?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.pr_watch_find_by_thread($1, $2)", cancellationToken, T(provider.Value), T(threadId)).ConfigureAwait(false)).SingleOrDefault();

    public Task<IReadOnlyList<PrWatch>> ListActiveAsync(CancellationToken cancellationToken) =>
        ReadAsync("SELECT * FROM agentd.pr_watch_list_active()", cancellationToken);

    public async Task<bool> StopAsync(long id, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.pr_watch_stop($1)");
        cmd.Parameters.Add(P(id, NpgsqlDbType.Bigint));
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task UpdateAsync(long id, int fixRounds, IReadOnlyList<int> seenComments, int? lastBuildId, DateTimeOffset? signalAt, PendingFix? pending, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.pr_watch_update($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.AddRange(new[]
        {
            P(id, NpgsqlDbType.Bigint), P(fixRounds, NpgsqlDbType.Integer), P(seenComments.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Integer),
            P(lastBuildId, NpgsqlDbType.Integer), P(signalAt, NpgsqlDbType.TimestampTz), P(pending is null ? null : JsonSerializer.Serialize(pending), NpgsqlDbType.Jsonb),
        });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PrWatch>> ReadAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<PrWatch>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            int O(string c) => r.GetOrdinal(c);
            rows.Add(new PrWatch(
                r.GetInt64(O("id")), r.GetString(O("repo")), r.GetInt32(O("pull_request_id")), r.GetString(O("title")), r.GetString(O("watched_by")),
                ProviderKey.From(r.GetString(O("provider"))), r.GetString(O("thread_id")), r.IsDBNull(O("space_id")) ? null : r.GetString(O("space_id")),
                r.GetString(O("status")) == "Watching", r.GetInt32(O("fix_rounds")), r.GetFieldValue<int[]>(O("seen_comments")),
                r.IsDBNull(O("last_build_id")) ? null : r.GetInt32(O("last_build_id")),
                r.IsDBNull(O("signal_at")) ? null : r.GetFieldValue<DateTimeOffset>(O("signal_at")),
                r.IsDBNull(O("pending")) ? null : JsonSerializer.Deserialize<PendingFix>(r.GetString(O("pending"))),
                r.GetFieldValue<DateTimeOffset>(O("created_at"))));
        }

        return rows;
    }

    private static NpgsqlParameter T(string? value) => P(value, NpgsqlDbType.Text);

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
