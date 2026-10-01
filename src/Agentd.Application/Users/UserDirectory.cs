using Agentd.Application.Abstractions;
using Agentd.Domain.Common;
using Agentd.Domain.Messaging;
using Agentd.Domain.Users;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Users;

/// <summary>A known user, as the allowlist and (from Phase 5) role checks see them.</summary>
public sealed record AgentdUser(UserId Id, string Name, IReadOnlyList<string> Roles, bool IsActive);

/// <summary>The user directory (PostgreSQL routines; cached in memory).</summary>
public interface IUserDirectory
{
    /// <summary>The user owning this chat identity, or null when it is unknown (not allowlisted).</summary>
    Task<AgentdUser?> FindByIdentityAsync(ProviderKey provider, string externalId, CancellationToken cancellationToken);

    /// <summary>Makes the directory match <paramref name="users"/>: upserts them and deactivates everyone else.</summary>
    Task SyncAsync(IReadOnlyList<User> users, CancellationToken cancellationToken);
}

/// <summary>Configuration section <c>Agentd:Users</c> (an array): the source of truth for the directory.</summary>
public sealed class UsersOptions
{
    public const string Section = "Agentd:Users";

    public IList<UserSeed> Items { get; } = [];
}

public sealed class UserSeed
{
    public string Name { get; set; } = "";

    public string? Email { get; set; }

    public IList<string> Roles { get; } = [];

    /// <summary>Chat identities by provider name, e.g. <c>"Discord": "7890…"</c>.</summary>
    public IDictionary<string, string> Identities { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<UserIdentity> ToIdentities() =>
        Identities.Select(i => new UserIdentity(ProviderKey.From(i.Key.ToLowerInvariant()), i.Value.Trim()));
}

/// <summary>Fails startup on a directory that would be ambiguous.</summary>
public sealed class UsersOptionsValidator : IValidateOptions<UsersOptions>
{
    public ValidateOptionsResult Validate(string? name, UsersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<(string, string), string>();
        foreach (var user in options.Items)
        {
            if (string.IsNullOrWhiteSpace(user.Name))
            {
                errors.Add("Agentd:Users: every user needs a Name.");
                continue;
            }

            if (!names.Add(user.Name.Trim()))
            {
                errors.Add($"Agentd:Users: the name '{user.Name}' is used twice.");
            }

            foreach (var (provider, externalId) in user.Identities)
            {
                if (!ProviderKey.Create(provider.ToLowerInvariant()).IsSuccess || string.IsNullOrWhiteSpace(externalId))
                {
                    errors.Add($"Agentd:Users:{user.Name}: identity '{provider}' needs a provider name (letters, digits, dashes) and an id.");
                }
                else if (!identities.TryAdd((provider.ToLowerInvariant(), externalId.Trim()), user.Name))
                {
                    errors.Add($"Agentd:Users: the {provider} identity {externalId} is mapped to both '{identities[(provider.ToLowerInvariant(), externalId.Trim())]}' and '{user.Name}'.");
                }
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>Seed the user directory from configuration (startup).</summary>
public sealed record SeedUsers;

public sealed class SeedUsersHandler(IUserDirectory directory, IOptions<UsersOptions> options) : ICommandHandler<SeedUsers, int>
{
    public async Task<Result<int>> Handle(SeedUsers command, CancellationToken cancellationToken)
    {
        var users = new List<User>();
        foreach (var seed in options.Value.Items)
        {
            var user = User.Create(seed.Name, seed.Email, seed.Roles.ToList(), seed.ToIdentities().ToList());
            if (!user.IsSuccess)
            {
                return user.Error;
            }

            users.Add(user.Value);
        }

        await directory.SyncAsync(users, cancellationToken).ConfigureAwait(false);
        return users.Count;
    }
}
