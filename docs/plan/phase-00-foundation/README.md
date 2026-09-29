# Phase 0 — Foundation

**Goal:** an empty but correctly shaped system. The solution follows Clean Architecture, **.NET
Aspire** orchestrates local development (PostgreSQL, the host and the Vite dev server), the Vue shell
is served with the agentd theme, and **GitHub Actions** CI is green. Everything later plugs into this.

Design refs: [Clean Architecture + BFF](../../architect/clean-architecture-bff.md) ·
[Architecture §2.1 / §7](../../architect/README.md) · [Design System](../../design-system/README.md)

## Decisions (from plan review)

| Question | Decision |
|---|---|
| CI host | **GitHub Actions** |
| Local orchestration | **.NET Aspire** (AppHost + ServiceDefaults), no docker compose |
| Node version | **Node 24** (pinned in `.nvmrc`, `package.json` `engines`, and CI) |
| Web package manager | **npm** (`package-lock.json` committed, `npm ci` in CI) |

---

## Scope

**In**

- The .NET 10 solution `Agentd.slnx`:
  - **application projects:** `Domain`, `Application`, `Infrastructure.Persistence`, `Bff`, `Mcp`,
    `Host`;
  - **Aspire projects:** `Agentd.AppHost` and `Agentd.ServiceDefaults`;
  - **test projects:** `Domain.Tests`, `Application.Tests`, `Infrastructure.Tests`, `Bff.Tests`,
    `ArchitectureTests`.
- `Directory.Build.props` (nullable, warnings as errors, analyzers), `Directory.Packages.props`
  (central package versions, including the Aspire SDK and integrations), and `global.json` (pins
  the .NET 10 SDK).
- **Aspire AppHost** (development orchestration only):
  - a **PostgreSQL** container resource with a persistent data volume, and an `agentd` database;
  - the **Host** project, with a reference to the database (`WithReference`, `WaitFor`);
  - the **Vite dev server** for `web/` through Aspire's JavaScript/Vite hosting integration, with
    its `/api`, `/bff`, `/hubs` and `/healthz` proxy pointing at the Host endpoint;
  - the **Aspire dashboard** for logs, traces and metrics in development.
- **ServiceDefaults:** OpenTelemetry (logs, traces, metrics → OTLP to the Aspire dashboard in dev),
  health checks (`/healthz` liveness + readiness), HTTP resilience defaults and service discovery.
- **Host:**
  - strongly typed options with `ValidateOnStart`, `appsettings*.json`, user-secrets for
    development;
  - `builder.AddServiceDefaults()`;
  - PostgreSQL via Aspire's Npgsql EF Core client integration (`AddNpgsqlDbContext<AgentdDbContext>("agentd")`),
    which adds pooling, health checks and tracing.
- **Runs without Aspire too:** in production, the Host takes `ConnectionStrings__agentd` and OTLP
  settings from environment or config and runs under systemd (Phase 10). **Aspire is a
  development-time orchestrator, not a runtime dependency.**
- **Persistence:** `AgentdDbContext`, a first migration (`jobs`, `events`), and migrations applied at
  startup in Development only.
- **`web/`:**
  - Vue 3 + TypeScript + Vite + Pinia + Vue Router + Tailwind 4 + daisyUI 5;
  - the **agentd green theme** (light and dark), an empty app shell (navbar, theme toggle) and
    `public/theme-init.js`;
  - `npm run build` → `Host/wwwroot`, served with an SPA fallback.
- **Architecture tests** for the dependency rule. `AppHost` and `ServiceDefaults` are excluded from
  the layer rules. Only `Host` may reference `ServiceDefaults`.
- **GitHub Actions** (`.github/workflows/ci.yml`):
  - `actions/setup-dotnet` (from `global.json`) + `actions/setup-node` (Node 24, npm cache);
  - `dotnet build` with warnings as errors, `dotnet format --verify-no-changes`, and `dotnet test`
    (Testcontainers PostgreSQL for the persistence tests);
  - `npm ci`, `vue-tsc --noEmit`, `npm run lint`, `npm run test` (vitest), `npm run build`;
  - runs on PRs and pushes to `main`; build and test results as check annotations.

**Out** (later phases): any Azure DevOps, Claude, chat, auth or real UI screens; production deploy
(Phase 10).

---

## Tasks

Detailed tasks: [tasks/README.md](tasks/README.md)

| ID | Task | Depends on | Size | Status |
|---|---|---|---|---|
| T0.1 | [Repository conventions & build settings](tasks/01-repo-conventions.md) | — | S | ☐ |
| T0.2 | [Solution & project scaffold](tasks/02-solution-scaffold.md) | T0.1 | S | ☐ |
| T0.3 | [Agentd.ServiceDefaults](tasks/03-service-defaults.md) | T0.2 | S | ☐ |
| T0.4 | [Agentd.AppHost (Aspire)](tasks/04-aspire-apphost.md) | T0.2, T0.3 | M | ☐ |
| T0.5 | [Host startup & options](tasks/05-host-startup-and-options.md) | T0.3 | S | ☐ |
| T0.6 | [Persistence baseline](tasks/06-persistence-baseline.md) | T0.4, T0.5 | M | ☐ |
| T0.7 | [Web scaffold, theme & shell](tasks/07-web-scaffold-and-theme.md) | T0.1 | M | ☐ |
| T0.8 | [Host serves the SPA](tasks/08-host-serves-spa.md) | T0.5, T0.7 | S | ☐ |
| T0.9 | [Architecture tests](tasks/09-architecture-tests.md) | T0.2 | S | ☐ |
| T0.10 | [CI with GitHub Actions](tasks/10-ci-github-actions.md) | T0.1–T0.9 | S | ☐ |
| T0.11 | [Developer README](tasks/11-developer-readme.md) | T0.4, T0.7, T0.10 | S | ☐ |

## Exit criteria (the demo)

- `dotnet run --project src/Agentd.AppHost` → the **Aspire dashboard** shows `postgres`, `agentd`
  (the Host) and `web` (Vite) as healthy. Opening the web endpoint shows the empty agentd shell in
  green, the light/dark toggle works, and edits to a `.vue` file hot-reload.
- `/healthz` is healthy, and it turns unhealthy when the PostgreSQL resource is stopped from the
  dashboard. Host logs and traces appear in the Aspire dashboard.
- **Without Aspire:** `ConnectionStrings__agentd=… dotnet run --project src/Agentd.Host` serves the
  built UI from `wwwroot`.
- Adding a forbidden reference (e.g. Application → EF Core) makes the architecture tests fail.
- The **GitHub Actions** CI run is green on the PR.

## Remaining open questions

- **Container runtime** for Aspire on the dev machine: Docker (assumed) or Podman?
- **Aspire version:** use the latest stable release at build time, pinned in
  `Directory.Packages.props`. Any constraint?
