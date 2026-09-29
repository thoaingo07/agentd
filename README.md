# agentd

**agentd** is a background daemon that picks up Azure DevOps work items tagged for AI, runs one
Claude Code agent per work item in its own git worktree (Design → Plan → Implement → Test → Review),
talks to developers through Discord/Telegram, and opens a pull request when the work is done.

- Design: [`docs/architect`](docs/architect/README.md)
- Implementation plan (phases & tasks): [`docs/plan`](docs/plan/README.md)
- Conventions for contributors and AI agents: [`AGENTS.md`](AGENTS.md)

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10 (see `global.json`) | |
| Node.js | 24 (see `.nvmrc`) | `nvm install && nvm use` |
| Docker | recent | Aspire runs PostgreSQL in a container; integration tests use Testcontainers |

## Run locally (Aspire)

```bash
(cd web && npm ci)
dotnet run --project src/Agentd.AppHost
```

The console prints the **Aspire dashboard** URL (with a login token). From the dashboard you can
open:

- **`agentd-host`**: **open the app here** (`http://127.0.0.1:7780`). The Host renders the Razor
  shell and proxies the Vite modules and hot reload (HMR) to the dev server;
- **`web`**: the Vite dev server (assets and HMR only; it isn't opened directly);
- **`postgres`**: PostgreSQL, with data kept in the `agentd-pgdata` volume.

Aspire runs **`agentd-migrator`** first, which applies the schema and exits. The Host starts after it
finishes. Stop everything with `Ctrl+C`.

## Run without Aspire (production-like)

```bash
export ConnectionStrings__agentd="Host=localhost;Database=agentd;Username=…;Password=…"
dotnet run --project src/Agentd.Migrator                   # apply schema migrations + routines, then exit
dotnet run --project src/Agentd.Host -p:BuildWeb=true      # builds web/ (assets + .vite/manifest.json) into wwwroot; Razor shell on 127.0.0.1:7780
```

## Tests

.NET tests use **MSTest on Microsoft.Testing.Platform**:

```bash
dotnet test                                        # everything (Integration tests need Docker)
dotnet test --filter "TestCategory!=Integration"   # fast unit tests only
dotnet run --project tests/Agentd.Domain.Tests     # run a single test project directly
(cd web && npm test)                               # web unit tests (vitest)
```

## Project layout

```
src/
  Agentd.Domain/                     domain model (no dependencies)
  Agentd.Application/                use cases + ports
  Agentd.Infrastructure.Persistence/ repositories: PostgreSQL routines via Npgsql (no ORM)
  Agentd.Migrator/                   standalone schema migrator: FluentMigrator + raw SQL + routines
  Agentd.Bff/                        backend-for-frontend: Razor SPA shell (Vite manifest / dev proxy), endpoints
  Agentd.Mcp/                        MCP tools (agents)
  Agentd.Host/                       composition root; wwwroot/ holds the Vite build output
  Agentd.ServiceDefaults/            OpenTelemetry, health checks, resilience
  Agentd.AppHost/                    .NET Aspire local orchestration (dev only)
tests/                               MSTest projects, one per layer, plus architecture tests
web/                                 Vue 3 + Vite + Pinia + Tailwind/daisyUI
```

The dependency rule (Clean Architecture) is enforced by `tests/Agentd.ArchitectureTests`; see
[Clean Architecture + BFF](docs/architect/clean-architecture-bff.md) and
[data access](docs/architect/data-access.md).

## Conventions

- Branches: `phase/NN-<name>` or `feat/<name>`; CI (GitHub Actions) must be green before merging.
- Package versions live in `Directory.Packages.props` (central package management).
- Secrets come only from environment variables or user-secrets.
