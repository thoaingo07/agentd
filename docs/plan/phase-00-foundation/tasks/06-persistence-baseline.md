# T0.6 — Persistence baseline (EF Core + PostgreSQL)

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 0 | T0.4, T0.5 | M | Agentd.Infrastructure.Persistence, Agentd.Host |

## Goal
An `AgentdDbContext` with the first migration (`jobs`, `events`), wired through Aspire's Npgsql EF
Core client integration, with migrations applied automatically **only in Development**.

## Files
- `src/Agentd.Infrastructure.Persistence/AgentdDbContext.cs`: create.
- `src/Agentd.Infrastructure.Persistence/Configurations/JobConfiguration.cs`, `EventConfiguration.cs`: create. Placeholder entities for now.
- `src/Agentd.Infrastructure.Persistence/Records/JobRecord.cs`, `EventRecord.cs`: create. Persistence records; the Domain `Job` arrives in Phase 1.
- `src/Agentd.Infrastructure.Persistence/DesignTimeDbContextFactory.cs`: create. For `dotnet ef`.
- `src/Agentd.Infrastructure.Persistence/Migrations/*`: create. `0001_Initial`.
- `src/Agentd.Infrastructure.Persistence/DependencyInjection.cs`: create. `AddPersistence()`.
- `src/Agentd.Host/Program.cs`: modify. `builder.AddNpgsqlDbContext<AgentdDbContext>("agentd")` + dev migration.
- `Directory.Packages.props`: modify. `Npgsql.EntityFrameworkCore.PostgreSQL`,
  `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design`,
  `EFCore.NamingConventions`, `Testcontainers.PostgreSql`.

## Implementation
1. Schema, in snake_case via `UseSnakeCaseNamingConvention()`:
   - `jobs`: `id bigint identity PK`, `work_item_id int not null`, `state text not null`,
     `created_at timestamptz`, `updated_at timestamptz`, and the `xmin` system column mapped as the
     concurrency token (`UseXminAsConcurrencyToken` or the current equivalent; verify).
   - `events`: `seq bigint identity PK`, `job_id bigint null FK → jobs`, `ts timestamptz not null`,
     `type text not null`, `payload jsonb not null`, index `(job_id, seq)`.
     Partitioning comes in Phase 3. Keep this a plain table now.
2. The Host registers the context with **Aspire's client integration**, which adds pooling, retries,
   a health check and tracing:
   ```csharp
   builder.AddNpgsqlDbContext<AgentdDbContext>("agentd",
       configureDbContextOptions: o => o.UseSnakeCaseNamingConvention());
   ```
   Outside Aspire, the same call reads `ConnectionStrings:agentd` from environment or config.
3. The migrations assembly is `Agentd.Infrastructure.Persistence`. The design-time factory reads
   `AGENTD_DESIGN_CONNECTION` for `dotnet ef migrations add`.
4. Development only:
   ```csharp
   if (app.Environment.IsDevelopment())
   {
       using var scope = app.Services.CreateScope();
       await scope.ServiceProvider.GetRequiredService<AgentdDbContext>().Database.MigrateAsync();
   }
   ```
   Production migrations are an explicit step (Phase 10 procedure).
5. Clean Architecture: the Application layer will define repository ports in Phase 1, and nothing
   outside Persistence references `AgentdDbContext` except the Host's registration.

## Tests
- `Infrastructure.Tests` (MSTest, `[TestCategory("Integration")]`): one Testcontainers PostgreSQL
  container per test assembly, started in `[AssemblyInitialize]` and disposed in
  `[AssemblyCleanup]`. Each test class gets its own database, created in `[ClassInitialize]`, so
  classes can run in parallel. The tests:
  - apply the migrations;
  - insert a job + event;
  - read them back, and check that the `jsonb` payload round-trips.
- A test that fails if there are model changes without a migration (`HasPendingModelChanges()`, or
  the current EF API; verify).

## Done when
- [ ] Under Aspire, the Host starts, the migration is applied, and `jobs` / `events` exist in the `agentd` DB.
- [ ] `/healthz` includes the Npgsql health check (unhealthy when the DB is stopped).
- [ ] The Testcontainers tests pass locally and in CI.
