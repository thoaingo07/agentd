using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>The repository registry via the <c>agentd.repository_*</c> routines.</summary>
public sealed class RepositoryStore(NpgsqlDataSource dataSource) : IRepositoryRegistry
{
    public Task<IReadOnlyList<Repository>> ListAsync(CancellationToken cancellationToken) =>
        QueryAsync("SELECT * FROM agentd.repository_list()", cancellationToken);

    public async Task<Repository?> GetAsync(RepositoryName name, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync("SELECT * FROM agentd.repository_get($1)", cancellationToken, Text(name.Value)).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<Result> UpsertAsync(Repository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        try
        {
            await QueryAsync(
                "SELECT * FROM agentd.repository_upsert($1, $2, $3, $4, $5, $6, $7, $8)",
                cancellationToken,
                Text(repository.Name.Value),
                Text(repository.RemoteUrl),
                Text(repository.AzureDevOps.Organization),
                Text(repository.AzureDevOps.Project),
                Text(repository.AzureDevOps.Name),
                Text(repository.BaseBranch),
                Text(repository.MatchTag),
                new NpgsqlParameter { Value = repository.MatchAreaPaths.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text }).ConfigureAwait(false);
            return Result.Ok;
        }
        catch (PostgresException ex) when (SqlErrors.ToDomainError(ex) is { } error)
        {
            return error;
        }
    }

    public async Task<bool> RemoveAsync(RepositoryName name, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.repository_remove($1)");
        cmd.Parameters.Add(Text(name.Value));
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<IReadOnlyList<Repository>> QueryAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<Repository>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var tag = r.GetOrdinal("match_tag");
            list.Add(new Repository(
                RepositoryName.From(r.GetString(r.GetOrdinal("name"))),
                r.GetString(r.GetOrdinal("remote_url")),
                new AzureDevOpsRepo(r.GetString(r.GetOrdinal("organization")), r.GetString(r.GetOrdinal("project")), r.GetString(r.GetOrdinal("repo"))),
                r.GetString(r.GetOrdinal("base_branch")),
                r.IsDBNull(tag) ? null : r.GetString(tag),
                r.GetFieldValue<string[]>(r.GetOrdinal("match_area_paths"))));
        }

        return list;
    }

    private static NpgsqlParameter Text(string? value) => new() { Value = value ?? (object)DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
}
