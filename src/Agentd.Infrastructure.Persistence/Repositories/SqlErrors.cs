using Agentd.Domain.Common;
using Npgsql;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>Maps the custom SQLSTATEs raised by agentd routines to domain errors (see data-access.md §3).</summary>
internal static class SqlErrors
{
    public const string NotFound = "AG404";
    public const string Conflict = "AG409";
    public const string InvalidTransition = "AG422";

    public static DomainError? ToDomainError(PostgresException ex) => ex.SqlState switch
    {
        NotFound => DomainError.NotFound(ex.MessageText),
        Conflict => DomainError.Conflict(ex.MessageText),
        InvalidTransition => new DomainError("invalid_transition", ex.MessageText),
        _ => null,
    };
}
