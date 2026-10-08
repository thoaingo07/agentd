using System.Text.Json;
using Agentd.Application.Ideas;
using Agentd.Application.Reviews;
using Agentd.Domain.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>PR reviews in chat via <c>agentd.pr_review_*</c> routines.</summary>
public sealed class ReviewStore(NpgsqlDataSource dataSource) : IReviewStore
{
    public async Task<long> InsertAsync(string repository, int pullRequestId, string title, string author, ProviderKey provider, string threadId, string? spaceId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.pr_review_insert($1, $2, $3, $4, $5, $6, $7)");
        cmd.Parameters.AddRange(new[] { T(repository), P(pullRequestId, NpgsqlDbType.Integer), T(title), T(author), T(provider.Value), T(threadId), T(spaceId) });
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<Review?> GetAsync(long id, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.pr_review_get($1)", cancellationToken, P(id, NpgsqlDbType.Bigint)).ConfigureAwait(false)).SingleOrDefault();

    public async Task<Review?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.pr_review_find_by_thread($1, $2)", cancellationToken, T(provider.Value), T(threadId)).ConfigureAwait(false)).SingleOrDefault();

    public async Task<IReadOnlyList<Review>> ListPostedAsync(CancellationToken cancellationToken) =>
        await ReadAsync("SELECT * FROM agentd.pr_review_list_posted()", cancellationToken).ConfigureAwait(false);

    public async Task<Review?> FindLatestAsync(string repository, int pullRequestId, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.pr_review_find_latest($1, $2)", cancellationToken, T(repository), P(pullRequestId, NpgsqlDbType.Integer)).ConfigureAwait(false)).SingleOrDefault();

    public async Task SaveAsync(Review review, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(review);
        await using var cmd = dataSource.CreateCommand("SELECT agentd.pr_review_update($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)");
        cmd.Parameters.AddRange(new[]
        {
            P(review.Id, NpgsqlDbType.Bigint), T(review.Status), T(review.HeadCommit), T(review.Focus), P(review.Session, NpgsqlDbType.Uuid),
            T(review.Model), T(review.Effort), T(review.Worktree),
            P(review.Result is null ? null : JsonSerializer.Serialize(review.Result), NpgsqlDbType.Jsonb),
            P(review.PostedThreads.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Integer),
        });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddMessageAsync(long reviewId, string direction, string author, string text, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.pr_review_message_add($1, $2, $3, $4)");
        cmd.Parameters.AddRange(new[] { P(reviewId, NpgsqlDbType.Bigint), T(direction), T(author), T(text) });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IdeaMessage>> ListMessagesAsync(long reviewId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT direction, author, text, at FROM agentd.pr_review_message_list($1)");
        cmd.Parameters.Add(P(reviewId, NpgsqlDbType.Bigint));
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var messages = new List<IdeaMessage>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new IdeaMessage(r.GetString(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)));
        }

        return messages;
    }

    public async Task<IReadOnlyList<string>> ListOpenThreadsAsync(ProviderKey provider, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT * FROM agentd.pr_review_list_open_threads($1)");
        cmd.Parameters.Add(T(provider.Value));
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var threads = new List<string>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            threads.Add(r.GetString(0));
        }

        return threads;
    }

    private async Task<IReadOnlyList<Review>> ReadAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<Review>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string? Str(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetString(r.GetOrdinal(c));
            var session = r.GetOrdinal("session_id");
            var result = Str("result");
            rows.Add(new Review(
                r.GetInt64(r.GetOrdinal("id")), r.GetString(r.GetOrdinal("repo")), r.GetInt32(r.GetOrdinal("pull_request_id")), r.GetString(r.GetOrdinal("title")),
                r.GetString(r.GetOrdinal("author")), ProviderKey.From(r.GetString(r.GetOrdinal("provider"))), r.GetString(r.GetOrdinal("thread_id")), Str("space_id"),
                r.GetString(r.GetOrdinal("status")), Str("head_commit"), Str("focus"), r.IsDBNull(session) ? null : r.GetGuid(session),
                Str("model"), Str("effort"), Str("worktree_path"),
                result is null ? null : JsonSerializer.Deserialize<ReviewResult>(result),
                r.GetFieldValue<int[]>(r.GetOrdinal("posted_threads"))));
        }

        return rows;
    }

    private static NpgsqlParameter T(string? value) => P(value, NpgsqlDbType.Text);

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
