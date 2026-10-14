# agentd

**agentd** is a background daemon that picks up Azure DevOps work items tagged for AI, runs one
Claude Code agent per work item in its own git worktree (Design → Plan → Implement → Test → Review),
talks to developers in its web UI (and, optionally, Discord or Telegram), and opens a pull request when the work is done.

- **User guide (install, configure, use): [`docs/public-ui`](docs/public-ui/README.md)**
- Design: [`docs/architect`](docs/architect/README.md)
- Implementation plan (phases & tasks): [`docs/plan`](docs/plan/README.md)
- Conventions for contributors and AI agents: [`AGENTS.md`](AGENTS.md)

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10 (see `global.json`) | |
| Node.js | 24 (see `.nvmrc`) | `nvm install && nvm use` |
| Docker | recent | Aspire runs PostgreSQL in a container; integration tests use Testcontainers |

## Run agentd standalone (no chat)

agentd doesn't need Discord or Telegram: the web UI does everything chat does. To run it that way:

1. **Install** it on the server ([getting started §1](docs/public-ui/getting-started.md#1-install-agentd)): the
   release's `install.sh`, Docker Compose, or from source as below.
2. **Set up** in the browser: `agentd daemon install && agentd daemon start`, open the one-time link from
   `agentd daemon logs` (over an SSH tunnel: `ssh -L 7780:127.0.0.1:7780 <server>`), and go through the wizard.
   Leave **Chat (optional)** turned off. Finish needs only the database, Azure DevOps and Claude.
3. **Restart** (`agentd daemon restart`) and open `http://127.0.0.1:7780`.

Then, from the web UI:

| You want to | Where |
|---|---|
| Answer the agent, approve a plan, allow a tool | **Inbox**: every message agentd sends you, threads waiting on you first, with their buttons and a reply box |
| Run a work item, follow it, cancel, retry, pause, hand off | **Dashboard** → the work item's page (transcript, diff, Conversation) |
| Brainstorm work items | **Ideas** |
| Review a branch or PR, ask about it, send findings, fix it | **Reviews** |
| Ask about the code | **Chat** |
| Read what agents published | **Artifacts**: pages are HTML files in `~/.agentd/artifacts` without S3 (Settings → Artifacts) |
| See every prompt sent to Claude | **Settings → Prompts** (Admins) |

Without chat, a finished job closes by itself (nothing to delete), and its conversation stays on its page and in the
Inbox. What stays chat-only: the `!` commands typed in a channel (each has a button on the web) and pings on your
phone; the Inbox shows a count in the nav instead. Turning chat on later works too: running jobs get threads.

## Run locally (Aspire)

```bash
(cd src/Agentd.Web && npm ci)
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

The Development config points at the sandbox (`ermsystem/Portal`, repository `sysmin`) with the
daemon loop **off** (`Agentd:Scheduler:Enabled=false`): turn it on to poll, claim and run agents.

## Run without Aspire (production-like)

```bash
export ConnectionStrings__agentd="Host=localhost;Database=agentd;Username=…;Password=…"
dotnet run --project src/Agentd.Migrator                   # apply schema migrations + routines, then exit
dotnet run --project src/Agentd.Host -p:BuildWeb=true      # builds the client apps (Agentd.Web/wwwroot + manifest.json), serves on 127.0.0.1:7780
```

## The `agentd` CLI

The Host builds the `agentd` executable. With no verb it runs the daemon; the verbs use the same
configuration and database:

```bash
dotnet run --project src/Agentd.Host -- --help
dotnet run --project src/Agentd.Host -- db migrate                     # apply the schema in-process
dotnet run --project src/Agentd.Host -- repo add git@ssh.dev.azure.com:v3/org/project/repo
dotnet run --project src/Agentd.Host -- repo list
dotnet run --project src/Agentd.Host -- run 1234 [--repo sysmin]       # queue a work item now
dotnet run --project src/Agentd.Host -- status [--all]
dotnet run --project src/Agentd.Host -- doctor                         # checks, each with a fix hint
dotnet run --project src/Agentd.Host -- daemon run
```

Configuration lives in the **config home** (`AGENTD_HOME`, default `~/.agentd`): `config/agentd.json`
uses the schema of the `Agentd` section (without the `Agentd` wrapper), and `AGENTD_*` variables
override it (`AGENTD_AzureDevOps__Organization`, `AGENTD_Database__ConnectionString`, …). Exit codes:
0 ok · 1 error · 2 usage · 3 not found / no repository match · 4 active job exists · 5 doctor failed.

## Tests

.NET tests use **MSTest on Microsoft.Testing.Platform**:

```bash
dotnet test                                        # everything (Integration tests need Docker)
dotnet test --filter "TestCategory!=Integration"   # fast unit tests only
dotnet run --project tests/Agentd.Domain.Tests     # run a single test project directly
(cd src/Agentd.Web && npm test)                               # web unit tests (vitest)
```

## Project layout

```
src/
  Agentd.Domain/                     domain model (no dependencies)
  Agentd.Application/                use cases + ports
  Agentd.Infrastructure.Persistence/ repositories: PostgreSQL routines via Npgsql (no ORM)
  Agentd.Migrator/                   standalone schema migrator: FluentMigrator + raw SQL + routines
  Agentd.Bff/                        backend-for-frontend: /bff + /api + SignalR for the web UI
  Agentd.Web/                        web UI: Razor layout + ViteHelper + ClientApps/<app> (Vue SPAs, package.json here)
  Agentd.Mcp/                        MCP tools (agents)
  Agentd.Host/                       composition root
  Agentd.ServiceDefaults/            OpenTelemetry, health checks, resilience
  Agentd.AppHost/                    .NET Aspire local orchestration (dev only)
tests/                               MSTest projects, one per layer, plus architecture tests
```

The dependency rule (Clean Architecture) is enforced by `tests/Agentd.ArchitectureTests`; see
[Clean Architecture + BFF](docs/architect/clean-architecture-bff.md) and
[data access](docs/architect/data-access.md).

## Conventions

- Branches: `phase/NN-<name>` or `feat/<name>`; CI (GitHub Actions) must be green before merging.
- Package versions live in `Directory.Packages.props` (central package management).
- Secrets come only from environment variables or user-secrets.
