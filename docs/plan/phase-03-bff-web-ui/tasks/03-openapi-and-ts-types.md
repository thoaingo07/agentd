# T3.3 — OpenAPI document and generated TypeScript types

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | T3.2 | S | Agentd.Bff / web |

## Goal
Make the BFF contract the single source of truth for the frontend. ASP.NET Core generates an
OpenAPI document from the BFF endpoints only, and `openapi-typescript` turns it into
`src/Agentd.Web/ClientApps/shared/api/schema.d.ts` (types only, no runtime code). CI fails if the committed types drift from
the server.

## Files
- `src/Agentd.Bff/BffModule.cs` — modify: `services.AddOpenApi("v1", …)`, `app.MapOpenApi("/openapi/{documentName}.json")`.
- `src/Agentd.Bff/OpenApi/BffOnlyDocumentFilter.cs` — create: keep only `/api/*` and `/bff/*` paths (exclude `/mcp`, `/healthz`, hubs).
- `src/Agentd.Web/package.json` — modify: `"gen:api"` script; `openapi-typescript` as a devDependency (version pinned by the lockfile).
- `src/Agentd.Web/ClientApps/shared/api/schema.d.ts` — create (generated, committed).
- `src/Agentd.Web/ClientApps/shared/api/types.ts` — create: friendly aliases.
- `.github/workflows/ci.yml` — modify: a drift check step.

## Implementation
1. Register the built-in OpenAPI support (`Microsoft.AspNetCore.OpenApi`) in the BFF. Add a
   document transformer or filter that drops non-BFF paths. Verify the transformer API against
   the pinned package version.
2. Serve the document only in `Development`, or to Admins later. In other environments,
   `/openapi/v1.json` returns 404, so the API shape isn't exposed publicly.
3. Give every endpoint an explicit `.WithName("GetDashboard")`, `.Produces<DashboardVm>()` and
   `.ProducesProblem(404)` so the operation IDs and types are stable.
4. **Generation without a running server:**
   - use the build-time document generation (`Microsoft.Extensions.ApiDescription.Server`, emitted on
     build to `artifacts/openapi/agentd.json`), so CI doesn't need to start the host;
   - if that proves awkward, fall back to starting the host in CI and fetching `/openapi/v1.json`.
5. `npm run gen:api` = `openapi-typescript ../artifacts/openapi/agentd.json -o src/api/schema.d.ts`.
6. `types.ts` exports aliases, e.g.
   `export type JobSummary = components['schemas']['JobSummaryVm']`, plus `JobDetail`, `AgentEvent`,
   `EventPage`, `Dashboard`, `HistoryPage` and `Diff`. It also defines a `JobState` union type,
   because OpenAPI enums may arrive as strings.
7. **CI drift check:** `dotnet build` → `npm run gen:api` → `git diff --exit-code src/Agentd.Web/ClientApps/shared/api/schema.d.ts`.
   If they differ, fail with the message "run `npm run gen:api` and commit".

## Tests
- CI drift check (above).
- `vue-tsc --noEmit` passes using the generated types from every store and view.
- A small vitest that imports `types.ts` and assigns a sample fixture (from the T3.2 snapshot
  tests) to catch shape mismatches at compile time.

## Done when
- [x] `/openapi/v1.json` (Development) contains only `/api` and `/bff` operations, with stable names.
- [x] `npm run gen:api` regenerates `schema.d.ts` without a running server.
- [x] CI fails on contract drift.
- [x] The frontend has no hand-written DTO interfaces that duplicate server types.

## As built
- **Document:** `Microsoft.AspNetCore.OpenApi` 10.0.12, with `BffOnlyDocumentTransformer` (keeps
  `/api` and `/bff`, drops `servers`). It's served at `/openapi/v1.json` in Development only, and is
  404 elsewhere. Every endpoint has `.WithName(...)` and `Produces*` metadata. The JSON
  `NumberHandling` is `Strict`, so ids are `integer` in the contract, not `integer | string`.
- **No build-time generation:** `Microsoft.Extensions.ApiDescription.Server` isn't used. It starts the
  Host's entry point, and that's the CLI. Instead, `Bff.Tests/OpenApiContractTests` builds the BFF
  in-process and compares the document with the committed
  `src/Agentd.Web/ClientApps/shared/api/openapi.json`. `AGENTD_UPDATE_OPENAPI=1` rewrites the file.
  So `dotnet test` catches server drift, and no running server is needed.
- **Types:** `npm run gen:api` turns `openapi.json` into `schema.d.ts`. The CI web job regenerates
  it and fails on any `git diff`.
  - `openapi-typescript` 7.13 declares a peer dependency on TypeScript 5. A `package.json` override
    points it at the project's TypeScript 6, and generation works unchanged.
- **`types.ts`:** aliases (`JobSummary`, `JobDetail`, `AgentEvent`, `EventPage`, `Dashboard`,
  `HistoryPage`, `Diff`, …), plus `Problem` (with the `code` extension) and the `JobState` union
  with `isJobState`.
- **Changing the contract:** change the endpoint or view model, run
  `AGENTD_UPDATE_OPENAPI=1 dotnet test --project tests/Agentd.Bff.Tests`, then `npm run gen:api`,
  and commit both generated files.
