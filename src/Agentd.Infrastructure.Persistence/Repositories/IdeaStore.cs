using System.Text.Json;
using Agentd.Application.Ideas;
using Agentd.Domain.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>Ideas and their conversation via <c>agentd.idea_*</c> routines.</summary>
public sealed class IdeaStore(NpgsqlDataSource dataSource) : IIdeaStore
{
    public async Task<long> InsertAsync(string repository, string title, string author, ProviderKey provider, string threadId, string? spaceId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.idea_insert($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.AddRange(new[] { T(repository), T(title), T(author), T(provider.Value), T(threadId), T(spaceId) });
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<Idea?> GetAsync(long id, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.idea_get($1)", cancellationToken, P(id, NpgsqlDbType.Bigint)).ConfigureAwait(false)).SingleOrDefault();

    public async Task<Idea?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.idea_find_by_thread($1, $2)", cancellationToken, T(provider.Value), T(threadId)).ConfigureAwait(false)).SingleOrDefault();

    public async Task SaveAsync(Idea idea, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idea);
        await using var cmd = dataSource.CreateCommand("SELECT agentd.idea_update($1, $2, $3, $4, $5, $6, $7, $8)");
        cmd.Parameters.AddRange(new[]
        {
            P(idea.Id, NpgsqlDbType.Bigint), T(idea.Status), P(idea.Session, NpgsqlDbType.Uuid), T(idea.Model), T(idea.Effort), T(idea.Worktree),
            P(idea.Drafts is null ? null : JsonSerializer.Serialize(idea.Drafts), NpgsqlDbType.Jsonb), P(idea.CreatedWorkItems.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Integer),
        });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddMessageAsync(long ideaId, string direction, string author, string text, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.idea_message_add($1, $2, $3, $4)");
        cmd.Parameters.AddRange(new[] { P(ideaId, NpgsqlDbType.Bigint), T(direction), T(author), T(text) });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IdeaMessage>> ListMessagesAsync(long ideaId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT * FROM agentd.idea_message_list($1)");
        cmd.Parameters.Add(P(ideaId, NpgsqlDbType.Bigint));
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var messages = new List<IdeaMessage>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new IdeaMessage(r.GetString(r.GetOrdinal("direction")), r.GetString(r.GetOrdinal("author")), r.GetString(r.GetOrdinal("text")), r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("at"))));
        }

        return messages;
    }

    private async Task<IReadOnlyList<Idea>> ReadAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<Idea>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string? Str(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetString(r.GetOrdinal(c));
            var session = r.GetOrdinal("session_id");
            var drafts = Str("drafts");
            rows.Add(new Idea(
                r.GetInt64(r.GetOrdinal("id")), r.GetString(r.GetOrdinal("repo")), r.GetString(r.GetOrdinal("title")), r.GetString(r.GetOrdinal("author")),
                ProviderKey.From(r.GetString(r.GetOrdinal("provider"))), r.GetString(r.GetOrdinal("thread_id")), Str("space_id"), r.GetString(r.GetOrdinal("status")),
                r.IsDBNull(session) ? null : r.GetGuid(session), Str("model"), Str("effort"), Str("worktree_path"),
                drafts is null ? null : JsonSerializer.Deserialize<List<WorkItemDraft>>(drafts),
                r.GetFieldValue<int[]>(r.GetOrdinal("created_work_items"))));
        }

        return rows;
    }

    private static NpgsqlParameter T(string? value) => P(value, NpgsqlDbType.Text);

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
