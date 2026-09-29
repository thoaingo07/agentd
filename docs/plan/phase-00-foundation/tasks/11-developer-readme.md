# T0.11 — Developer README

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 0 | T0.4, T0.7, T0.10 | S | repo root |

## Goal
A new contributor can go from clone to a running, healthy system in under 10 minutes by following
the root README.

## Files
- `README.md`: modify. It currently contains only `# agentd`.

## Implementation
Sections:
1. **What agentd is**: two sentences, plus links to `docs/architect/README.md` and `docs/plan/README.md`.
2. **Prerequisites**: the .NET 10 SDK (matching `global.json`), Node 24 (`nvm use`), and Docker (or
   Podman) running, for the Aspire containers.
3. **Run locally**:
   ```bash
   (cd web && npm ci)
   dotnet run --project src/Agentd.AppHost
   ```
   Then open the Aspire dashboard URL printed in the console. The `web` resource link opens the UI.
4. **Run without Aspire** (production-like):
   ```bash
   export ConnectionStrings__agentd="Host=localhost;Database=agentd;Username=…;Password=…"
   dotnet run --project src/Agentd.Host -p:BuildWeb=true
   ```
5. **Tests**: `dotnet test` (needs Docker for Testcontainers), and `cd web && npm test`.
6. **Project layout**: a short tree with one line per project, linking to the Clean Architecture doc.
7. **Conventions**: branch names `phase/NN-name` or `feat/…`; CI must be green; architecture tests
   are the dependency rule.

## Tests
- A fresh clone on a second machine (or a clean container) following only the README reaches a
  healthy dashboard.

## Done when
- [ ] The README's steps work exactly as written on a clean checkout.
- [ ] The links resolve.
