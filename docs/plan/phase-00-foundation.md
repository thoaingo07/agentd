# Phase 0 — Foundation

**Goal:** an empty but correctly shaped system. The solution follows Clean Architecture, **.NET
Aspire** orchestrates local development (PostgreSQL, the host and the Vite dev server), the Vue shell
is served with the agentd theme, and **GitHub Actions** CI is green. Everything later plugs into this.

Design refs: [Clean Architecture + BFF](../architect/clean-architecture-bff.md) ·
[Architecture §2.1 / §7](../architect/README.md) · [Design System](../design-system/README.md)

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

1. `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.nvmrc` (24).
2. Scaffold the solution and projects with their project references (dependencies pointing inward only).
3. `Agentd.ServiceDefaults`: OTel, health checks, resilience, service discovery.
4. `Agentd.AppHost`: the PostgreSQL resource (+ volume) → `agentd` DB → the Host (`WaitFor`) → the
   Vite app (`web/`).
5. Host:
   - the `AgentdOptions` tree with validation;
   - `AddServiceDefaults()` and `AddNpgsqlDbContext`;
   - a `Web.Urls` default of `127.0.0.1:7780` when not run by Aspire.
6. Persistence: DbContext + migration; development-only apply-on-startup.
7. `web/` scaffold:
   - the theme CSS from the design system §5 and the app shell;
   - `npm run build` → `../src/Agentd.Host/wwwroot`;
   - a Vite proxy target taken from the Aspire-provided environment variable, falling back to
     `http://127.0.0.1:7780`.
8. The Host serves static assets + `MapFallbackToFile("index.html")`, and maps `/healthz` from
   ServiceDefaults.
9. `Agentd.ArchitectureTests` (Domain has no references; Application has no EF, ASP.NET or SDK
   references; Bff and Mcp have no Infrastructure references; only Host references ServiceDefaults).
10. `.github/workflows/ci.yml` as above.
11. Root `README.md`: prerequisites (.NET 10 SDK, Node 24, Docker or Podman for Aspire containers)
    and `dotnet run --project src/Agentd.AppHost`.

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
