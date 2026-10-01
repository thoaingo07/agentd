using System.Text.RegularExpressions;
using Agentd.Domain.Common;

namespace Agentd.Domain.Messaging;

/// <summary>Identifies a messaging provider instance, e.g. <c>discord</c> or <c>telegram-team</c> (lower-case <c>[a-z0-9-]+</c>).</summary>
public readonly partial record struct ProviderKey
{
    private ProviderKey(string value) => Value = value;

    public string Value { get; }

    public static Result<ProviderKey> Create(string? value) =>
        value is not null && Pattern().IsMatch(value)
            ? new ProviderKey(value)
            : DomainError.Validation($"Provider key must be lower-case letters, digits and dashes (was '{value}').");

    /// <summary>For trusted sources (e.g. database rows) where the value was validated on the way in.</summary>
    public static ProviderKey From(string value) =>
        Create(value) is { IsSuccess: true } r ? r.Value : throw new ArgumentException($"Invalid provider key '{value}'.", nameof(value));

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Pattern();
}
