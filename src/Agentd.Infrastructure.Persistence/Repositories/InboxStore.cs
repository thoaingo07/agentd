using System.Text.Json;
using Agentd.Application.Inbox;
using Agentd.Application.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>The Inbox via <c>agentd.inbox_*</c> routines (docs/architect/inbox.md).</summary>
public sealed class InboxStore(NpgsqlDataSource dataSource) : IInbox
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public async Task<long> AddAsync(InboxEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var options = entry.Message.Options is { Count: > 0 } o ? JsonSerializer.Serialize(o.Select(x => new { id = x.Id, label = x.Label }), s_json) : null;
        await using var cmd = dataSource.CreateCommand("SELECT agentd.inbox_add($1, $2, $3, $4, $5, $6, $7)");
        cmd.Parameters.AddRange(new[]
        {
            T(entry.Source), P(entry.SourceId, NpgsqlDbType.Bigint), T(entry.Title), T(entry.Recipient), T(entry.Message.Kind.ToString()),
            T(entry.Message.Markdown), P(options, NpgsqlDbType.Jsonb),
        });
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<InboxThread>> ThreadsAsync(string user, string? recipient, int limit, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT source, source_id, title, recipient, latest_id, latest_kind, latest_preview, latest_at, items, unread, waiting, question_id, question, question_options " +
            "FROM agentd.inbox_threads($1, $2, $3)");
        cmd.Parameters.AddRange(new[] { T(user), T(recipient), P(limit, NpgsqlDbType.Integer) });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<InboxThread>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var (source, sourceId, title, to) = (reader.GetString(0), reader.GetInt64(1), reader.GetString(2), S(reader, 3));
            var question = reader.IsDBNull(11) ? null
                : new InboxItem(reader.GetInt64(11), source, sourceId, title, to, nameof(MessageKind.Question), reader.GetString(12), Options(S(reader, 13)), default);
            rows.Add(new InboxThread(source, sourceId, title, to, reader.GetInt64(4), reader.GetString(5), reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetBoolean(10), question));
        }

        return rows;
    }

    public async Task<IReadOnlyList<InboxItem>> ThreadAsync(string source, long sourceId, long? before, int limit, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT id, source, source_id, title, recipient, kind, markdown, options, created_at FROM agentd.inbox_thread($1, $2, $3, $4)");
        cmd.Parameters.AddRange(new[] { T(source), P(sourceId, NpgsqlDbType.Bigint), P(before, NpgsqlDbType.Bigint), P(limit, NpgsqlDbType.Integer) });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<InboxItem>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new InboxItem(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), S(reader, 4), reader.GetString(5),
                reader.GetString(6), Options(S(reader, 7)), reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return rows;
    }

    public async Task MarkReadAsync(string user, string source, long sourceId, long until, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.inbox_mark_read($1, $2, $3, $4)");
        cmd.Parameters.AddRange(new[] { T(user), T(source), P(sourceId, NpgsqlDbType.Bigint), P(until, NpgsqlDbType.Bigint) });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<InboxCounts> CountsAsync(string user, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT waiting, unread FROM agentd.inbox_counts($1)");
        cmd.Parameters.Add(T(user));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new InboxCounts(reader.GetInt32(0), reader.GetInt32(1)) : new InboxCounts(0, 0);
    }

    public async Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.inbox_delete_before($1)");
        cmd.Parameters.Add(P(before, NpgsqlDbType.TimestampTz));
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static List<MessageOption> Options(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<List<OptionDto>>(json, s_json)?.Select(o => new MessageOption(o.Id, o.Label)).ToList() ?? [];

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static NpgsqlParameter T(string? value) => P(value, NpgsqlDbType.Text);

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => PgParameter.Of(value, type);

    private sealed record OptionDto(string Id, string Label);
}
