using FluentMigrator;

namespace Agentd.Migrator.Migrations;

// One line per migration; the SQL lives in Migrations/{version}_{name}.up.sql (+ optional .down.sql).
// Versions are yyyyMMddNNNN so migrations from parallel branches don't collide.

[Migration(2026_09_29_0001, "Initial schema: jobs, events")]
public sealed class InitialSchema : SqlMigration;

[Migration(2026_09_30_0001, "Walking skeleton: full job record, one active job per work item")]
public sealed class WalkingSkeleton : SqlMigration;

[Migration(2026_09_30_0002, "Repositories registered by URL")]
public sealed class Repositories : SqlMigration;
