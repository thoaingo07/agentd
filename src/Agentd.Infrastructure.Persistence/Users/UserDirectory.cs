using System.Collections.Concurrent;
using System.Text.Json;
using Agentd.Application.Users;
using Agentd.Domain.Messaging;
using Agentd.Domain.Users;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Persistence.Users;

/// <summary>
/// The user directory via <c>agentd.user_*</c> routines. Lookups are cached per identity and the cache
/// is cleared on every sync (the directory only changes through configuration).
/// </summary>
public sealed class UserDirectory(NpgsqlDataSource dataSource) : IUserDirectory
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<(string Provider, string ExternalId), AgentdUser?> _cache = new();

    public async Task<AgentdUser?> FindByIdentityAsync(ProviderKey provider, string externalId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue((provider.Value, externalId), out var cached))
        {
            return cached;
        }

        await using var cmd = dataSource.CreateCommand("SELECT id, name, roles, is_active FROM agentd.user_find_by_identity($1, $2)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = provider.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = externalId, NpgsqlDbType = NpgsqlDbType.Text });
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var user = await r.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AgentdUser(new UserId(r.GetInt64(0)), r.GetString(1), r.GetFieldValue<string[]>(2), r.GetBoolean(3))
            : null;
        _cache[(provider.Value, externalId)] = user;
        return user;
    }

    public async Task SyncAsync(IReadOnlyList<User> users, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(users);
        var payload = JsonSerializer.Serialize(
            users.Select(u => new
            {
                name = u.Name,
                email = u.Email,
                roles = u.Roles,
                identities = u.Identities.Select(i => new { provider = i.Provider.Value, externalId = i.ExternalId }),
            }),
            s_json);
        await using var cmd = dataSource.CreateCommand("SELECT agentd.user_sync($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = payload, NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _cache.Clear();
    }
}
