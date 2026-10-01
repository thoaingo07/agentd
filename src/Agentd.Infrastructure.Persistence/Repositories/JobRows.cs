using System.Text.Json;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Npgsql;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>Maps <c>agentd.jobs</c> rows (by column name) to snapshots, and domain events to the routines' JSON.</summary>
internal static class JobRows
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static async Task<List<JobSnapshot>> ReadAllAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        var rows = new List<JobSnapshot>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    public static string EventsJson(IReadOnlyList<IDomainEvent> events) =>
        JsonSerializer.Serialize(events.Select(e => new { type = e.GetType().Name, payload = (object)e }), s_json);

    private static JobSnapshot Map(NpgsqlDataReader r)
    {
        string? Str(string column) => r.IsDBNull(r.GetOrdinal(column)) ? null : r.GetString(r.GetOrdinal(column));

        var prTitle = Str("pr_title");
        var draft = prTitle is null ? null : PullRequestDraft.Create(prTitle, Str("pr_description"), Str("pr_summary")).Value;
        var sessionOrdinal = r.GetOrdinal("claude_session_id");
        var notBeforeOrdinal = r.GetOrdinal("not_before");

        return new JobSnapshot(
            new JobId(r.GetInt64(r.GetOrdinal("id"))),
            WorkItemId.From(r.GetInt32(r.GetOrdinal("work_item_id"))),
            RepositoryName.From(r.GetString(r.GetOrdinal("repo"))),
            r.GetString(r.GetOrdinal("title")),
            Enum.Parse<JobState>(r.GetString(r.GetOrdinal("state"))),
            Str("branch") is { } branch ? BranchName.From(branch) : null,
            Str("worktree_path") is { } path ? new WorktreePath(path) : null,
            r.IsDBNull(sessionOrdinal) ? null : new ClaudeSessionId(r.GetGuid(sessionOrdinal)),
            Str("pr_url") is { } url ? new PullRequestUrl(new Uri(url)) : null,
            draft,
            r.GetInt32(r.GetOrdinal("attempt")),
            r.GetInt32(r.GetOrdinal("resume_count")),
            r.GetInt32(r.GetOrdinal("publish_attempts")),
            Str("last_error"),
            r.IsDBNull(notBeforeOrdinal) ? null : r.GetFieldValue<DateTimeOffset>(notBeforeOrdinal),
            r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("created_at")),
            r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("updated_at")),
            r.GetInt64(r.GetOrdinal("version")),
            r.GetFieldValue<string[]>(r.GetOrdinal("pending_messages")));
    }
}
