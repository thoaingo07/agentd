using FluentMigrator;

namespace Agentd.Migrator.Routines;

/// <summary>
/// Re-applies every routine script (<c>Routines/**/*.sql</c>, all <c>CREATE OR REPLACE</c>) after the
/// versioned migrations, on every run, in one transaction. Routines are idempotent, so there is nothing
/// to track; a changed file simply wins. Changing a routine's signature or return type needs a
/// versioned migration that drops the old routine first.
/// </summary>
[Maintenance(MigrationStage.AfterAll, TransactionBehavior.Default)]
public sealed class ApplyRoutines : ForwardOnlyMigration
{
    public override void Up()
    {
        foreach (var path in SqlResources.List("Routines"))
        {
            Execute.Sql(SqlResources.Read(path));
        }
    }
}
