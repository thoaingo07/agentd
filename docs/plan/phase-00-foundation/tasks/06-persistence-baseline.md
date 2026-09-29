# T0.6 — Persistence baseline (Npgsql + SQL migrations + routines)

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 0 | T0.4, T0.5 | M | Agentd.Infrastructure.Persistence, Agentd.Host |

> **Superseded after Phase 0:** the schema moved to the standalone **`Agentd.Migrator`** project,
> which uses **FluentMigrator with raw SQL scripts**, and the Host no longer migrates. See
> [data-access.md §2 and §4](../../../architect/data-access.md#4-running-migrations-agentdmigrator).
> This task is kept as the Phase 0 record.

## Goal
The data-access foundation from [data-access.md](../../../architect/data-access.md):
- a pooled `NpgsqlDataSource` registered through **Aspire's Npgsql client integration**;
- the **`DatabaseMigrator`** (versioned SQL migrations + repeatable routines, under an advisory lock);
- the first migration (`jobs`, `events`) and one routine per table, proving the pattern.

There is **no EF Core**.

## Files
- `src/Agentd.Infrastructure.Persistence/Agentd.Infrastructure.Persistence.csproj`: modify.
  Package `Npgsql` (Dapper is added in Phase 1, together with the first repository that maps rows);
  `<EmbeddedResource Include="Database\**\*.sql" LogicalName="%(RecursiveDir)%(Filename)%(Extension)" />`,
  so resource names are paths like `Migrations/0001_initial.sql`.
- `src/Agentd.Infrastructure.Persistence/Database/Migrations/0001_initial.sql`: create.
- `src/Agentd.Infrastructure.Persistence/Database/Routines/job/job_create.sql`: create. The proof-of-pattern function.
- `src/Agentd.Infrastructure.Persistence/Database/Routines/event/event_append.sql`: create.
- `src/Agentd.Infrastructure.Persistence/Migrations/DatabaseMigrator.cs`: create. The runner.
- `src/Agentd.Infrastructure.Persistence/Migrations/SqlScript.cs`: create. Loads embedded scripts + SHA-256.
- `src/Agentd.Infrastructure.Persistence/DependencyInjection.cs`: create. `AddPersistence()`.
- `src/Agentd.Host/Program.cs`: modify. `builder.AddNpgsqlDataSource("agentd")` and the
  Development-only migration.
- `Directory.Packages.props`: modify. `Npgsql`, `Dapper`, `Aspire.Npgsql`, `Testcontainers.PostgreSql`.

## Implementation
1. `0001_initial.sql`:
   ```sql
   CREATE SCHEMA IF NOT EXISTS agentd;
   CREATE TABLE agentd.jobs (
     id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
     work_item_id  int         NOT NULL,
     state         text        NOT NULL,
     created_at    timestamptz NOT NULL DEFAULT now(),
     updated_at    timestamptz NOT NULL DEFAULT now(),
     version       bigint      NOT NULL DEFAULT 1
   );
   CREATE TABLE agentd.events (
     seq      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
     job_id   bigint NULL REFERENCES agentd.jobs(id),
     ts       timestamptz NOT NULL DEFAULT now(),
     type     text  NOT NULL,
     payload  jsonb NOT NULL
   );
   CREATE INDEX ix_events_job_seq ON agentd.events (job_id, seq);
   ```
   The bookkeeping tables `agentd.schema_migrations` and `agentd.schema_routines` are created by the
   runner itself, not by a migration.
2. Routines (they prove the conventions; Phase 1 extends them):
   - `agentd.job_create(p_work_item_id int) RETURNS TABLE (id bigint, state text, version bigint)`;
   - `agentd.event_append(p_job_id bigint, p_type text, p_payload jsonb) RETURNS bigint` (the new `seq`).
3. `DatabaseMigrator.MigrateAsync(ct)`, following [data-access.md §4](../../../architect/data-access.md#4-migration-runner-databasemigrator):
   - `pg_advisory_lock(hashtext('agentd.migrate'))` on a dedicated connection;
   - versioned scripts are applied in order, **one transaction each**, recording `version` + `checksum`;
     a changed checksum on an applied script throws `MigrationChecksumMismatchException`;
   - routines are re-applied when new or changed (checksum in `schema_routines`), in one transaction;
   - everything applied is logged with `ILogger`.
4. Host wiring:
   ```csharp
   builder.AddNpgsqlDataSource("agentd");        // Aspire.Npgsql: pooling, health check, tracing
   builder.Services.AddPersistence();            // migrator (+ repositories from Phase 1)
   …
   if (app.Environment.IsDevelopment())
       await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
   ```
   Outside Aspire, `AddNpgsqlDataSource("agentd")` reads `ConnectionStrings:agentd`. Production runs
   migrations as an explicit step (`agentd db migrate`, Phase 10 procedure).
5. **Thread safety:** only `NpgsqlDataSource` (thread-safe, pooled) is a singleton. Every operation
   does `await using var conn = await dataSource.OpenConnectionAsync(ct)`. No connection or command
   is stored in a field.

## Tests
- `Infrastructure.Tests` (MSTest, `[TestCategory("Integration")]`):
  - one Testcontainers PostgreSQL per assembly, started in `[AssemblyInitialize]`, which also runs
    `DatabaseMigrator`; disposed in `[AssemblyCleanup]`;
  - **migrator:** a second run is a no-op; editing an applied script → `MigrationChecksumMismatchException`;
    a changed routine is re-applied; two migrators started in parallel → one waits on the advisory
    lock, and nothing is applied twice;
  - **routines:** `job_create` returns version 1; `event_append` returns increasing `seq` values;
    the `jsonb` payload round-trips;
  - **parallelism:** 50 parallel `event_append` calls from separate tasks all succeed, with distinct `seq`s.

## Done when
- [ ] Under Aspire, the Host starts, the migrator applies `0001` + the routines, and
      `agentd.jobs` / `agentd.events` exist.
- [ ] `/healthz` includes the Npgsql health check (unhealthy when the DB is stopped).
- [ ] The integration tests pass locally and in CI.
- [ ] No EF Core package is referenced anywhere in the solution.
