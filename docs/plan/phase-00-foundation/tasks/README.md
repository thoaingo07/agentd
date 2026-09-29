# Phase 0 — Tasks

Detailed implementation tasks for [Phase 0 — Foundation](../README.md). Each file lists the files to touch, the implementation steps, the tests and a done-when checklist.

| ID | Task | Depends on | Size | Status |
|---|---|---|---|---|
| T0.1 | [Repository conventions & build settings](01-repo-conventions.md) | — | S | ☑ |
| T0.2 | [Solution & project scaffold](02-solution-scaffold.md) | T0.1 | S | ☑ |
| T0.3 | [Agentd.ServiceDefaults](03-service-defaults.md) | T0.2 | S | ☑ |
| T0.4 | [Agentd.AppHost (Aspire)](04-aspire-apphost.md) | T0.2, T0.3 | M | ☑ |
| T0.5 | [Host startup & options](05-host-startup-and-options.md) | T0.3 | S | ☑ |
| T0.6 | [Persistence baseline](06-persistence-baseline.md) | T0.4, T0.5 | M | ☑ |
| T0.7 | [Web scaffold, theme & shell](07-web-scaffold-and-theme.md) | T0.1 | M | ☑ |
| T0.8 | [Host serves the SPA](08-host-serves-spa.md) | T0.5, T0.7 | S | ☑ |
| T0.9 | [Architecture tests](09-architecture-tests.md) | T0.2 | S | ☑ |
| T0.10 | [CI with GitHub Actions](10-ci-github-actions.md) | T0.1–T0.9 | S | ☑ |
| T0.11 | [Developer README](11-developer-readme.md) | T0.4, T0.7, T0.10 | S | ☑ |

**Suggested order:** T0.1 → T0.2 → (T0.3, T0.7, T0.9 in parallel) → T0.4 → T0.5 → T0.6 → T0.8 → T0.10 → T0.11.
