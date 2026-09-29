// Local development orchestration: PostgreSQL → Migrator (run to completion) → the agentd Host → the Vite dev server.
// Aspire is dev-time only; production runs Agentd.Host directly (see docs/plan/phase-10).
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("agentd-pgdata");

var database = postgres.AddDatabase("agentd");

// Applies migrations + routines once, then exits; the Host starts only after it succeeded.
var migrator = builder.AddProject<Projects.Agentd_Migrator>("agentd-migrator")
    .WithReference(database)
    .WaitFor(database);

var host = builder.AddProject<Projects.Agentd_Host>("agentd-host")
    .WithReference(database)
    .WaitForCompletion(migrator)
    .WithHttpHealthCheck("/healthz");

builder.AddViteApp("web", "../../web")
    .WithReference(host)
    .WaitFor(host);

await builder.Build().RunAsync().ConfigureAwait(false);
