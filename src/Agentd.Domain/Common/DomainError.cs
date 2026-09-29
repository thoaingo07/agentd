namespace Agentd.Domain.Common;

/// <summary>An expected failure, returned (not thrown) from domain and application operations.</summary>
public sealed record DomainError(string Code, string Message)
{
    public static DomainError InvalidTransition(object from, string operation) =>
        new("invalid_transition", $"Cannot {operation} from state {from}.");

    public static DomainError NotFound(string what) => new("not_found", $"{what} was not found.");

    public static DomainError Conflict(string message) => new("conflict", message);

    public static DomainError Validation(string message) => new("validation", message);

    public override string ToString() => $"{Code}: {Message}";
}
