using Agentd.Application.Ports;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>Delegated Azure DevOps sign-ins via <c>agentd.ado_connection_*</c> routines.</summary>
public sealed class AdoUserConnectionStore(NpgsqlDataSource dataSource) : IAdoUserConnections
{
    public async Task UpsertAsync(Guid identityId, string uniqueName, string displayName, string webLogin, byte[] refreshToken, AdoConnectionKind kind, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.ado_connection_upsert($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.AddRange(new[] { Id(identityId), T(uniqueName), T(displayName), T(webLogin), Bytes(refreshToken), T(kind.ToString()) });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetCommitAuthorAsync(Guid identityId, string webLogin, string? name, string? email, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.ado_connection_set_commit_author($1, $2, $3, $4)");
        cmd.Parameters.AddRange(new[] { Id(identityId), T(webLogin), OptionalT(name), OptionalT(email) });
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<AdoUserConnection?> FindByIdentityAsync(Guid identityId, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.ado_connection_find_by_identity($1)", cancellationToken, Id(identityId)).ConfigureAwait(false)).SingleOrDefault();

    public async Task<AdoUserConnection?> FindByUniqueNameAsync(string uniqueName, CancellationToken cancellationToken) =>
        (await ReadAsync("SELECT * FROM agentd.ado_connection_find_by_unique_name($1)", cancellationToken, T(uniqueName)).ConfigureAwait(false)).SingleOrDefault();

    public Task<IReadOnlyList<AdoUserConnection>> ListByWebLoginAsync(string webLogin, CancellationToken cancellationToken) =>
        ReadAsync("SELECT * FROM agentd.ado_connection_list_by_web_login($1)", cancellationToken, T(webLogin));

    public async Task<bool> StoreTokenAsync(Guid identityId, byte[] refreshToken, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.ado_connection_store_token($1, $2)");
        cmd.Parameters.AddRange(new[] { Id(identityId), Bytes(refreshToken) });
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task MarkFailedAsync(Guid identityId, string reason, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.ado_connection_mark_failed($1, $2)");
        cmd.Parameters.AddRange(new[] { Id(identityId), T(reason) });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid identityId, string webLogin, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT agentd.ado_connection_delete($1, $2)");
        cmd.Parameters.AddRange(new[] { Id(identityId), T(webLogin) });
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<IReadOnlyList<AdoUserConnection>> ReadAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<AdoUserConnection>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var error = r.GetOrdinal("last_error");
            var commitName = r.GetOrdinal("commit_name");
            var commitEmail = r.GetOrdinal("commit_email");
            rows.Add(new AdoUserConnection(
                r.GetGuid(r.GetOrdinal("ado_identity_id")), r.GetString(r.GetOrdinal("unique_name")), r.GetString(r.GetOrdinal("display_name")),
                r.GetString(r.GetOrdinal("web_login")), r.GetFieldValue<byte[]>(r.GetOrdinal("refresh_token")),
                r.GetString(r.GetOrdinal("status")) == "Failed", r.IsDBNull(error) ? null : r.GetString(error),
                r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("connected_at")), r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("refreshed_at")),
                Enum.Parse<AdoConnectionKind>(r.GetString(r.GetOrdinal("kind"))),
                r.IsDBNull(commitName) ? null : r.GetString(commitName), r.IsDBNull(commitEmail) ? null : r.GetString(commitEmail)));
        }

        return rows;
    }

    private static NpgsqlParameter Id(Guid value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Uuid };

    private static NpgsqlParameter T(string value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter OptionalT(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Bytes(byte[] value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Bytea };
}
