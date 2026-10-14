using System.Text.Json;
using Agentd.Application.Reviews;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>A review page's Sends, kept for the re-checks (<c>agentd.review_session_post_*</c>).</summary>
public sealed class ReviewSessionPostStore(NpgsqlDataSource dataSource) : IReviewSessionPosts
{
    private static readonly JsonSerializerOptions s_web = new(JsonSerializerDefaults.Web);

    public async Task SetAsync(long sessionId, int pullRequestId, ReviewResult posted, string? head, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.review_session_post_set($1, $2, $3, $4)");
        cmd.Parameters.AddRange(new[] { P(sessionId, NpgsqlDbType.Bigint), P(pullRequestId, NpgsqlDbType.Integer), P(JsonSerializer.Serialize(posted), NpgsqlDbType.Jsonb), P(head, NpgsqlDbType.Text) });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReviewSessionPost?> GetAsync(long sessionId, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.review_session_post_get($1)", cancellationToken, P(sessionId, NpgsqlDbType.Bigint)).ConfigureAwait(false)).SingleOrDefault();

    public async Task<IReadOnlyList<ReviewSessionPost>> ListOpenAsync(CancellationToken cancellationToken) =>
        await ReadAsync("SELECT * FROM agentd.review_session_post_list_open()", cancellationToken).ConfigureAwait(false);

    public async Task UpdateAsync(long sessionId, ReviewResult posted, string? head, SessionRecheck? recheck, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.review_session_post_update($1, $2, $3, $4)");
        cmd.Parameters.AddRange(new[]
        {
            P(sessionId, NpgsqlDbType.Bigint), P(JsonSerializer.Serialize(posted), NpgsqlDbType.Jsonb), P(head, NpgsqlDbType.Text),
            P(recheck is null ? null : JsonSerializer.Serialize(recheck, s_web), NpgsqlDbType.Jsonb),
        });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DoneAsync(long sessionId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.review_session_post_done($1)");
        cmd.Parameters.Add(P(sessionId, NpgsqlDbType.Bigint));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ReviewSessionPost>> ReadAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<ReviewSessionPost>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var head = r.GetOrdinal("head");
            rows.Add(new ReviewSessionPost(
                r.GetInt64(r.GetOrdinal("session_id")), r.GetInt32(r.GetOrdinal("pull_request_id")),
                JsonSerializer.Deserialize<ReviewResult>(r.GetString(r.GetOrdinal("posted")))!,
                r.IsDBNull(head) ? null : r.GetString(head),
                JsonSerializer.Deserialize<List<SessionRecheck>>(r.GetString(r.GetOrdinal("rechecks")), s_web) ?? [],
                r.GetBoolean(r.GetOrdinal("done"))));
        }

        return rows;
    }

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
