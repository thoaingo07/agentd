using Agentd.Domain.Common;
using Agentd.Domain.Messaging;

namespace Agentd.Domain.Users;

/// <summary>Database identity of a user.</summary>
public readonly record struct UserId(long Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A person allowed to talk to agentd (directory v0: seeded from configuration). Roles are stored now
/// and enforced from Phase 5.
/// </summary>
public sealed record User(UserId Id, string Name, string? Email, IReadOnlyList<string> Roles, bool IsActive, IReadOnlyList<UserIdentity> Identities)
{
    public static Result<User> Create(string? name, string? email, IReadOnlyList<string>? roles, IReadOnlyList<UserIdentity>? identities) =>
        string.IsNullOrWhiteSpace(name)
            ? DomainError.Validation("User name is required.")
            : new User(default, name.Trim(), string.IsNullOrWhiteSpace(email) ? null : email.Trim(), roles ?? [], true, identities ?? []);
}

/// <summary>A user's account on one messaging provider (e.g. a Discord user id).</summary>
public sealed record UserIdentity(ProviderKey Provider, string ExternalId);
