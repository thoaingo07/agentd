using Agentd.Application.Chats;
using Agentd.Domain.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>Chats and their conversation via <c>agentd.chat_*</c> routines.</summary>
public sealed class ChatStore(NpgsqlDataSource dataSource) : IChatStore
{
    public async Task<long> InsertAsync(string author, ProviderKey provider, string threadId, string? spaceId, IReadOnlyList<string> repositories, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.chat_insert($1, $2, $3, $4, $5)");
        cmd.Parameters.AddRange(new[] { T(author), T(provider.Value), T(threadId), T(spaceId), P(repositories.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text) });
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<Chat?> GetAsync(long id, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.chat_get($1)", cancellationToken, P(id, NpgsqlDbType.Bigint)).ConfigureAwait(false)).SingleOrDefault();

    public async Task<Chat?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.chat_find_by_thread($1, $2)", cancellationToken, T(provider.Value), T(threadId)).ConfigureAwait(false)).SingleOrDefault();

    public async Task SaveAsync(Chat chat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        await using var cmd = dataSource.CreateCommand("SELECT agentd.chat_update($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.AddRange(new[]
        {
            P(chat.Id, NpgsqlDbType.Bigint), T(chat.Status), P(chat.Session, NpgsqlDbType.Uuid), T(chat.Model), T(chat.Effort),
            P(chat.Worktrees.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text),
        });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddMessageAsync(long chatId, string direction, string author, string text, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.chat_message_add($1, $2, $3, $4)");
        cmd.Parameters.AddRange(new[] { P(chatId, NpgsqlDbType.Bigint), T(direction), T(author), T(text) });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListOpenThreadsAsync(ProviderKey provider, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT * FROM agentd.chat_open_threads($1)");
        cmd.Parameters.Add(T(provider.Value));
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var threads = new List<string>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            threads.Add(r.GetString(0));
        }

        return threads;
    }

    private async Task<IReadOnlyList<Chat>> ReadAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<Chat>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string? Str(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetString(r.GetOrdinal(c));
            var session = r.GetOrdinal("session_id");
            rows.Add(new Chat(
                r.GetInt64(r.GetOrdinal("id")), r.GetString(r.GetOrdinal("author")), ProviderKey.From(r.GetString(r.GetOrdinal("provider"))),
                r.GetString(r.GetOrdinal("thread_id")), Str("space_id"), r.GetString(r.GetOrdinal("status")),
                r.GetFieldValue<string[]>(r.GetOrdinal("repos")), r.IsDBNull(session) ? null : r.GetGuid(session),
                Str("model"), Str("effort"), r.GetFieldValue<string[]>(r.GetOrdinal("worktrees"))));
        }

        return rows;
    }

    private static NpgsqlParameter T(string? value) => P(value, NpgsqlDbType.Text);

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
