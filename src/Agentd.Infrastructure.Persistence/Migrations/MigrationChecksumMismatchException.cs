namespace Agentd.Infrastructure.Persistence.Migrations;

/// <summary>Thrown when an already-applied migration script was modified. Applied migrations are immutable.</summary>
public sealed class MigrationChecksumMismatchException : Exception
{
    public MigrationChecksumMismatchException()
    {
    }

    public MigrationChecksumMismatchException(string message)
        : base(message)
    {
    }

    public MigrationChecksumMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
