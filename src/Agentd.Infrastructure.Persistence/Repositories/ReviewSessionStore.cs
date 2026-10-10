using System.Text.Json;
using Agentd.Application.Reviews;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>Review sessions via <c>agentd.review_session_*</c>, <c>review_comment_*</c> and <c>review_ask_*</c> routines.</summary>
public sealed class ReviewSessionStore(NpgsqlDataSource dataSource) : IReviewSessionStore
{
    public async Task<long> InsertAsync(string repository, string target, int? pullRequestId, string? headRef, string? baseRef, string createdBy, string? model, string? effort, CancellationToken cancellationToken) =>
        await ScalarAsync<long>("SELECT agentd.review_session_insert($1, $2, $3, $4, $5, $6, $7, $8)", cancellationToken,
            T(repository), T(target), P(pullRequestId, NpgsqlDbType.Integer), T(headRef), T(baseRef), T(createdBy), T(model), T(effort)).ConfigureAwait(false);

    public async Task<ReviewSession?> GetAsync(long id, CancellationToken cancellationToken) =>
        (await SessionsAsync("SELECT * FROM agentd.review_session_get($1)", cancellationToken, P(id, NpgsqlDbType.Bigint)).ConfigureAwait(false)).SingleOrDefault();

    public Task<IReadOnlyList<ReviewSession>> ListAsync(string createdBy, int limit, CancellationToken cancellationToken) =>
        SessionsAsync("SELECT * FROM agentd.review_session_list($1, $2)", cancellationToken, T(createdBy), P(limit, NpgsqlDbType.Integer));

    public Task<IReadOnlyList<ReviewSession>> ListByStatusAsync(string status, CancellationToken cancellationToken) =>
        SessionsAsync("SELECT * FROM agentd.review_session_list_by_status($1)", cancellationToken, T(status));

    public Task PinAsync(long id, string baseCommit, string headCommit, string? worktree, CancellationToken cancellationToken) =>
        ExecuteAsync("SELECT agentd.review_session_pin($1, $2, $3, $4)", cancellationToken, P(id, NpgsqlDbType.Bigint), T(baseCommit), T(headCommit), T(worktree));

    public Task SetStatusAsync(long id, string status, string? reason, string? sentTo, CancellationToken cancellationToken) =>
        ExecuteAsync("SELECT agentd.review_session_set_status($1, $2, $3, $4)", cancellationToken, P(id, NpgsqlDbType.Bigint), T(status), T(reason), T(sentTo));

    public async Task<int> AddFindingsAsync(long id, IReadOnlyList<SessionFinding> findings, string? summary, CancellationToken cancellationToken) =>
        await ScalarAsync<int>("SELECT agentd.review_session_add_findings($1, $2, $3)", cancellationToken,
            P(id, NpgsqlDbType.Bigint), P(JsonSerializer.Serialize(findings), NpgsqlDbType.Jsonb), T(summary)).ConfigureAwait(false);

    public async Task<bool> DecideAsync(long id, int index, string decision, string? edited, CancellationToken cancellationToken) =>
        await ScalarAsync<bool>("SELECT agentd.review_session_decide($1, $2, $3, $4)", cancellationToken,
            P(id, NpgsqlDbType.Bigint), P(index, NpgsqlDbType.Integer), T(decision), T(edited)).ConfigureAwait(false);

    public async Task<long> AddCommentAsync(long sessionId, string? file, int? line, int? endLine, string text, string author, CancellationToken cancellationToken) =>
        await ScalarAsync<long>("SELECT agentd.review_comment_add($1, $2, $3, $4, $5, $6)", cancellationToken,
            P(sessionId, NpgsqlDbType.Bigint), T(file), P(line, NpgsqlDbType.Integer), P(endLine, NpgsqlDbType.Integer), T(text), T(author)).ConfigureAwait(false);

    public async Task<bool> DeleteCommentAsync(long sessionId, long commentId, string author, CancellationToken cancellationToken) =>
        await ScalarAsync<bool>("SELECT agentd.review_comment_delete($1, $2, $3)", cancellationToken,
            P(sessionId, NpgsqlDbType.Bigint), P(commentId, NpgsqlDbType.Bigint), T(author)).ConfigureAwait(false);

    public async Task<bool> UpdateCommentAsync(long sessionId, long commentId, string author, string text, CancellationToken cancellationToken) =>
        await ScalarAsync<bool>("SELECT agentd.review_comment_update($1, $2, $3, $4)", cancellationToken,
            P(sessionId, NpgsqlDbType.Bigint), P(commentId, NpgsqlDbType.Bigint), T(author), T(text)).ConfigureAwait(false);

    public async Task<IReadOnlyList<ReviewComment>> ListCommentsAsync(long sessionId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT id, file, line, end_line, text, author, created_at FROM agentd.review_comment_list($1)");
        cmd.Parameters.Add(P(sessionId, NpgsqlDbType.Bigint));
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<ReviewComment>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ReviewComment(r.GetInt64(0), Str(r, 1), Int(r, 2), Int(r, 3), r.GetString(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)));
        }

        return rows;
    }

    public async Task<long> AddAskAsync(long sessionId, string? file, int? line, int? endLine, string question, string author, long? threadId, CancellationToken cancellationToken) =>
        await ScalarAsync<long>("SELECT agentd.review_ask_add($1, $2, $3, $4, $5, $6, $7)", cancellationToken,
            P(sessionId, NpgsqlDbType.Bigint), T(file), P(line, NpgsqlDbType.Integer), P(endLine, NpgsqlDbType.Integer), T(question), T(author), P(threadId, NpgsqlDbType.Bigint)).ConfigureAwait(false);

    public Task SetAskSessionAsync(long threadId, Guid session, CancellationToken cancellationToken) =>
        ExecuteAsync("SELECT agentd.review_ask_set_session($1, $2)", cancellationToken, P(threadId, NpgsqlDbType.Bigint), P(session, NpgsqlDbType.Uuid));

    public Task AnswerAsync(long askId, string answer, CancellationToken cancellationToken) =>
        ExecuteAsync("SELECT agentd.review_ask_answer($1, $2)", cancellationToken, P(askId, NpgsqlDbType.Bigint), T(answer));

    public async Task<IReadOnlyList<ReviewAsk>> ListAsksAsync(long sessionId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT id, file, line, end_line, question, answer, author, asked_at, answered_at, thread_id, agent_session FROM agentd.review_ask_list($1)");
        cmd.Parameters.Add(P(sessionId, NpgsqlDbType.Bigint));
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<ReviewAsk>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ReviewAsk(r.GetInt64(0), Str(r, 1), Int(r, 2), Int(r, 3), r.GetString(4), Str(r, 5), r.GetString(6),
                r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetGuid(10)));
        }

        return rows;
    }

    private async Task<IReadOnlyList<ReviewSession>> SessionsAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<ReviewSession>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string? S(string column) => Str(r, r.GetOrdinal(column));
            int? I(string column) => Int(r, r.GetOrdinal(column));
            var prReview = r.GetOrdinal("pr_review_id");
            rows.Add(new ReviewSession(
                r.GetInt64(r.GetOrdinal("id")), r.GetString(r.GetOrdinal("repo")), r.GetString(r.GetOrdinal("target")), I("pull_request_id"),
                S("head_ref"), S("base_ref"), S("base_commit"), S("head_commit"), r.GetString(r.GetOrdinal("status")), S("error"),
                S("model"), S("effort"), S("summary"),
                JsonSerializer.Deserialize<List<SessionFinding>>(r.GetString(r.GetOrdinal("findings"))) ?? [],
                r.IsDBNull(prReview) ? null : r.GetInt64(prReview), S("worktree_path"), r.GetString(r.GetOrdinal("created_by")), S("sent_to"),
                r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("created_at")), r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("updated_at"))));
        }

        return rows;
    }

    private async Task<TResult> ScalarAsync<TResult>(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        return (TResult)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static int? Int(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    private static NpgsqlParameter T(string? value) => P(value, NpgsqlDbType.Text);

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
