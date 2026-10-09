# AGENTS.md

Instructions for AI coding agents (and humans) working in the **agentd** repository.
The design lives in [`docs/architect`](docs/architect/README.md), and the phased build plan in [`docs/plan`](docs/plan/README.md).

## Testing (.NET)

- **Use MSTest on Microsoft.Testing.Platform (MTP). Don't use xUnit, NUnit or VSTest.**
- Test projects use the MSTest project SDK: `<Project Sdk="MSTest.Sdk">`. The version is pinned once
  in `global.json` under `msbuild-sdks`; don't put a version in the `.csproj`.
- `dotnet test` runs in MTP mode (`global.json` → `"test": { "runner": "Microsoft.Testing.Platform" }`).
  A single project can also run directly: `dotnet run --project tests/<Project>`.
- Write tests with `[TestClass]`, `[TestMethod]`, `[DataRow]` / `[DynamicData]`, and
  `[TestInitialize]` / `[ClassInitialize]` / `[AssemblyInitialize]`.
- **Assertions:** use MSTest's built-in `Assert`, `CollectionAssert` and `StringAssert` only. No
  third-party assertion libraries.
- Categories:
  - `[TestCategory("Integration")]`: needs a container runtime (Testcontainers PostgreSQL): Docker, or rootless
    **podman** with `DOCKER_HOST=unix:///run/user/$(id -u)/podman/podman.sock` (the dev machine's default).
  - `[TestCategory("Aspire")]`: starts the Aspire AppHost, and is excluded in CI.
- Common commands:
  ```bash
  dotnet test                                          # everything
  dotnet test --filter "TestCategory!=Integration"     # fast unit tests only
  dotnet test --filter "TestCategory!=Aspire" --report-trx --coverage   # what CI runs
  ```
- Web (`src/Agentd.Web/`) tests use **vitest**, and browser end-to-end tests use **Playwright**, run through npm.

## Stack & conventions (decided)

- **.NET 10**, **PostgreSQL**.
- **Data access: PostgreSQL functions and procedures only. There is NO EF Core or other ORM.**
  See [`docs/architect/data-access.md`](docs/architect/data-access.md).
  - Call routines with Npgsql, using typed positional parameters and never string concatenation.
    Dapper is allowed for mapping results only.
  - **The schema lives in the standalone `src/Agentd.Migrator` project (FluentMigrator + raw SQL).**
    - New migration: add `Migrations/{yyyyMMddNNNN}_{name}.up.sql` (plus an optional `.down.sql`)
      and a one-line `[Migration(yyyy_MM_dd_NNNN, "…")] public sealed class X : SqlMigration;` in
      `Migrations/Versions.cs`.
    - Applied migrations are immutable.
    - Routines are repeatable `CREATE OR REPLACE` scripts in `Routines/**`, re-applied on every run.
    - The Host never migrates. Run `dotnet run --project src/Agentd.Migrator`; Aspire does this
      automatically.
  - **Thread safety:** `NpgsqlDataSource` is the only singleton. Open one connection per operation
    and never share a connection or command across threads.
  - Every routine gets integration tests, including a parallel-callers test for concurrency-critical
    ones.
- **Clean Architecture + BFF:** dependencies point inward only (Domain ← Application ← Infrastructure /
  Presentation). The architecture tests in `tests/Agentd.ArchitectureTests` enforce this; never
  weaken them to make a build pass.
- **Local dev:** .NET Aspire (`dotnet run --project src/Agentd.AppHost`); with podman, also set
  `DOTNET_ASPIRE_CONTAINER_RUNTIME=podman`. Aspire is for development
  only; the Host must also run without it.
- **CI:** GitHub Actions. **Node 24**, **npm** (`npm ci`; `package-lock.json` committed).
- **Web UI:**
  - Vue 3 + TypeScript + Vite, Pinia setup stores (no TanStack or other data-fetching libraries),
    daisyUI 5 on Tailwind 4 (the agentd green theme), and base-ui-vue for reusable `Ag*` components;
  - follow [`docs/ui`](docs/ui/README.md) and [`docs/design-system`](docs/design-system/README.md);
  - **`src/Agentd.Web`** holds all Vue apps (`ClientApps/<app>/main.ts`, one `package.json` at the
    project root), `Views/Shared/_Layout.cshtml` and `ViteHelper`. The HTML shell is a Razor view; there's no
    `index.html`. Production reads `wwwroot/manifest.json` (served under `/_content/Agentd.Web/`);
    Development proxies that prefix, including HMR, to the Vite dev server (`UseProxyToSpaDevelopmentServer`). Open the app on the Host URL, not
    the Vite port.
- **Browser security:**
  - strict CSP with no inline scripts or styles and no `eval`;
  - never use `v-html`;
  - HttpOnly cookies only, and antiforgery on every unsafe `/api` or `/bff` request.

  See [`docs/security`](docs/security/README.md).
- **Central package management:** add package versions to `Directory.Packages.props`, never to
  individual `.csproj` files. Don't invent package versions; use the latest stable release, and
  check it.
- **Secrets** only ever come from environment variables or user-secrets. Never commit them to
  `appsettings*.json`.

## Working in this repo

- Follow the current phase's task files in `docs/plan/phase-NN-*/tasks/`. Each task has a
  "Done when" checklist.
- If the implementation deviates from `docs/architect`, update the design doc in the same change.
- Branches: `phase/NN-<name>` or `feat/<name>`. CI must be green before merging.
