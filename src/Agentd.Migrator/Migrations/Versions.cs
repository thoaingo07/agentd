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

[Migration(2026_09_30_0003, "Publish retry attempts")]
public sealed class PublishAttempts : SqlMigration;

[Migration(2026_10_01_0001, "Messaging: WaitingForHuman, conversations, outbox, inbound idempotency, users")]
public sealed class Messaging : SqlMigration;

[Migration(2026_10_03_0001, "Wait for human: waiting_since and wait_reminders")]
public sealed class WaitForHuman : SqlMigration;

[Migration(2026_10_04_0001, "Plan approval: plan_status and plan_estimate")]
public sealed class PlanApproval : SqlMigration;

[Migration(2026_10_05_0001, "Review loop: InReview state, fix_rounds, review_state")]
public sealed class ReviewLoop : SqlMigration;

[Migration(2026_10_06_0001, "Knowledge hand-off: handoff_status")]
public sealed class Handoff : SqlMigration;

[Migration(2026_10_07_0001, "Events partitioned by month")]
public sealed class EventsPartitioned : SqlMigration;

[Migration(2026_10_08_0001, "History indexes on jobs.updated_at")]
public sealed class HistoryIndex : SqlMigration;

[Migration(2026_10_09_0001, "Work item history indexes")]
public sealed class WorkItemHistory : SqlMigration;

[Migration(2026_10_10_0001, "Permission requests and remembered approvals")]
public sealed class Permissions : SqlMigration;

[Migration(2026_10_11_0001, "Brainstormed ideas and their conversation")]
public sealed class Ideas : SqlMigration;

[Migration(2026_10_12_0001, "Paused job state")]
public sealed class Paused : SqlMigration;
