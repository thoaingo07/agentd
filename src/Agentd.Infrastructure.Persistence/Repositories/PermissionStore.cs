using Agentd.Application.Permissions;
using Agentd.Domain.Jobs.ValueObjects;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>Permission requests and remembered approvals via <c>agentd.permission_*</c> routines.</summary>
public sealed class PermissionStore(NpgsqlDataSource dataSource) : IPermissionStore
{
    public async Task<long> InsertAsync(JobId jobId, string toolName, string summary, IReadOnlyList<string> ruleKeys, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.permission_request_insert($1, $2, $3, $4)");
        cmd.Parameters.AddRange(new[] { P(jobId.Value, NpgsqlDbType.Bigint), P(toolName, NpgsqlDbType.Text), P(summary, NpgsqlDbType.Text), P(ruleKeys.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text) });
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<PermissionRequest?> DecideAsync(long id, string status, string? scope, string decidedBy, RepositoryName repository, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.permission_request_decide($1, $2, $3, $4, $5)", cancellationToken,
            P(id, NpgsqlDbType.Bigint), P(status, NpgsqlDbType.Text), P(scope, NpgsqlDbType.Text), P(decidedBy, NpgsqlDbType.Text), P(repository.Value, NpgsqlDbType.Text)).ConfigureAwait(false))
        .SingleOrDefault();

    public async Task<PermissionRequest?> GetAsync(long id, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.permission_request_get($1)", cancellationToken, P(id, NpgsqlDbType.Bigint)).ConfigureAwait(false)).SingleOrDefault();

    public Task<IReadOnlyList<PermissionRequest>> ListPendingAsync(JobId jobId, CancellationToken cancellationToken) =>
        ReadAsync("SELECT * FROM agentd.permission_request_list_pending($1)", cancellationToken, P(jobId.Value, NpgsqlDbType.Bigint));

    public async Task<IReadOnlyList<string>> RuleKeysAsync(RepositoryName repository, JobId jobId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT * FROM agentd.permission_rule_keys($1, $2)");
        cmd.Parameters.AddRange(new[] { P(repository.Value, NpgsqlDbType.Text), P(jobId.Value, NpgsqlDbType.Bigint) });
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var keys = new List<string>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(r.GetString(0));
        }

        return keys;
    }

    public async Task<IReadOnlyDictionary<JobId, int>> PendingCountsAsync(CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT job_id, pending FROM agentd.permission_request_pending_counts()");
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<JobId, int>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[new JobId(r.GetInt64(0))] = r.GetInt32(1);
        }

        return counts;
    }

    public async Task<IReadOnlyList<PermissionRule>> ListRulesAsync(CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT id, repo, job_id, rule_key, created_by, created_at FROM agentd.permission_rule_list()");
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rules = new List<PermissionRule>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(new PermissionRule(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : new JobId(r.GetInt64(2)), r.GetString(3), r.GetString(4),
                r.GetFieldValue<DateTimeOffset>(5)));
        }

        return rules;
    }

    public async Task<bool> DeleteRuleAsync(long id, string by, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.permission_rule_delete($1, $2)");
        cmd.Parameters.AddRange(new[] { P(id, NpgsqlDbType.Bigint), P(by, NpgsqlDbType.Text) });
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<IReadOnlyList<PermissionRequest>> ReadAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<PermissionRequest>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string? Str(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetString(r.GetOrdinal(c));
            rows.Add(new PermissionRequest(
                r.GetInt64(r.GetOrdinal("id")),
                new JobId(r.GetInt64(r.GetOrdinal("job_id"))),
                r.GetString(r.GetOrdinal("tool_name")),
                r.GetString(r.GetOrdinal("summary")),
                r.GetFieldValue<string[]>(r.GetOrdinal("rule_keys")),
                r.GetString(r.GetOrdinal("status")),
                Str("scope"),
                Str("decided_by"),
                r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("requested_at"))));
        }

        return rows;
    }

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
