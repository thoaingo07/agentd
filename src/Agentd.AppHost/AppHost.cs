// Local development orchestration: PostgreSQL + the agentd Host + the Vite dev server.
// Aspire is dev-time only; production runs Agentd.Host directly (see docs/plan/phase-10).
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("agentd-pgdata");

var database = postgres.AddDatabase("agentd");

var host = builder.AddProject<Projects.Agentd_Host>("agentd-host")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/healthz");

builder.AddViteApp("web", "../../web")
    .WithReference(host)
    .WaitFor(host);

await builder.Build().RunAsync().ConfigureAwait(false);
