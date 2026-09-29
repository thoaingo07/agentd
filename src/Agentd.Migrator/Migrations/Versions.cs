using FluentMigrator;

namespace Agentd.Migrator.Migrations;

// One line per migration; the SQL lives in Migrations/{version}_{name}.up.sql (+ optional .down.sql).
// Versions are yyyyMMddNNNN so migrations from parallel branches don't collide.

[Migration(2026_09_29_0001, "Initial schema: jobs, events")]
public sealed class InitialSchema : SqlMigration;
