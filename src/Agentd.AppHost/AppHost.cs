// Local development orchestration: PostgreSQL → Migrator (run to completion) → the agentd Host (+ Vite dev server for assets).
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

// Vite serves modules + HMR only; the page itself is rendered by the Host (Razor), which proxies
// Vite paths to this dev server, so the browser only ever talks to the Host's origin.
var web = builder.AddViteApp("web", "../Agentd.Web");
host.WithReference(web);

await builder.Build().RunAsync().ConfigureAwait(false);
