# T3.2 — BFF endpoints and view models

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | T3.1, Phase 2 (`SubmitDeveloperMessage`) | M | Agentd.Application (queries) / Agentd.Bff |

## Goal
Expose the screen-shaped API the Vue app needs, as thin minimal-API endpoints over Application
use cases. The endpoints return **BFF view models**, never Domain or Application types. Unsafe
methods are grouped under `/api` so the antiforgery filter (T3.5) covers them automatically.

## Files
- `src/Agentd.Application/Queries/GetDashboard.cs`, `GetJob.cs`, `GetJobEvents.cs`, `GetJobDiff.cs`, `SearchHistory.cs` — create: query handlers + read models.
- `src/Agentd.Bff/BffModule.cs` — create: `AddBff()` / `MapBff()` extensions, which the Host calls.
- `src/Agentd.Bff/Endpoints/DashboardEndpoints.cs`, `JobEndpoints.cs`, `HistoryEndpoints.cs`, `WorkItemEndpoints.cs` — create.
- `src/Agentd.Bff/ViewModels/*.cs` — create: `DashboardVm`, `JobSummaryVm`, `JobDetailVm`, `EventVm`, `EventPageVm`, `DiffVm`, `HistoryPageVm`.
- `src/Agentd.Bff/Http/ResultExtensions.cs` — create: `Result<T>` → `IResult` (ProblemDetails).
- `src/Agentd.Host/Program.cs` — modify: `builder.AddBff(); app.MapBff();`.

## Implementation
1. **Endpoints** (all `RouteGroupBuilder api = app.MapGroup("/api")`):

   | Method | Route | Handler | Returns |
   |---|---|---|---|
   | GET | `/api/dashboard` | `GetDashboard` | `DashboardVm { stats, activeJobs[] }` |
   | GET | `/api/jobs/{id:long}` | `GetJob` | `JobDetailVm` (links to work item, conversations, PR) |
   | GET | `/api/jobs/{id:long}/events?after=&before=&limit=` | `GetJobEvents` | `EventPageVm { events[], oldestSeq, newestSeq, hasMore }` |
   | GET | `/api/jobs/{id:long}/diff` | `GetJobDiff` (via `IWorktreeManager`) | `DiffVm { baseRef, headRef, unifiedDiff }` |
   | GET | `/api/history?state=&repo=&q=&page=&pageSize=` | `SearchHistory` | `HistoryPageVm` |
   | POST | `/api/jobs/{id:long}/cancel` | `CancelJob` | 204 |
   | POST | `/api/jobs/{id:long}/retry` | `RetryJob` | 204 |
   | POST | `/api/jobs/{id:long}/messages` | `SubmitDeveloperMessage` (source = `web`) | 202 |
   | POST | `/api/workitems/{id:long}/run` | `ClaimWorkItem` | 202 + `{ jobId }` |

2. **Validation:**
   - `after` and `before` are mutually exclusive; `limit` defaults to 200 and must be between 1 and 500.
   - `pageSize` must be at most 100.
   - The message body is `{ text: string }` with 1–8,000 characters.
   - Invalid input → `400 ValidationProblem`.
3. **Error mapping** (`ResultExtensions.ToHttpResult`): `NotFound` → 404, `InvalidTransition` → 409
   with a `code` extension, `Validation` → 400. Unexpected exceptions go to the standard
   ProblemDetails middleware (500) with no stack trace outside Development.
4. **View models:**
   - `JobSummaryVm`: `id, workItemId, title, repo, branch, state, phase?, startedAt, elapsedSeconds,
     turns, costUsd, prUrl?, waitingSince?`.
   - `EventVm`: `seq, jobId, ts, type, payload`, where the payload is JSON passed through after
     server-side redaction.
   - Numbers that can exceed 2^53 (none today) must be strings.
5. **Diff:** capped at 2 MB. Beyond that, return `truncated: true` and the file list only.
6. **Caching:** `Cache-Control: no-store` for all `/api/*` responses. This is set centrally in T3.6,
   and must not be overridden here.
7. **Web messages** are posted with `SubmitDeveloperMessage(jobId, text, source: Web, user)`.
   Mirroring to chat comes from Phase 2's `MessagingService`, with no extra work here.
8. **Auth placeholder:** the group is `.RequireAuthorization()`. With `Auth.Mode = None`, a
   `LocalUserAuthenticationHandler` authenticates every loopback request as the pseudo-user
   `local` (with the Admin role), so the Phase 5 policies drop in without changing endpoints.

## Tests
- `Bff.Tests` (`WebApplicationFactory` + Testcontainers PostgreSQL):
  - `/api/dashboard` returns only non-final jobs, waiting ones first;
  - event paging with `after` and with `before`;
  - both `after` and `before` → 400;
  - `/api/jobs/999` → 404 ProblemDetails;
  - cancelling a `Done` job → 409 with `code = invalid_transition`;
  - `POST /messages` on a waiting job → 202, and the job resumes (the fake `IAgentRunner` receives
    the text).
- Snapshot test of the JSON shape of each view model, which guards the TS contract.

## Done when
- [ ] Every endpoint in the table exists, is thin (bind → handler → map), and returns view models.
- [ ] No endpoint references Infrastructure types; the architecture tests still pass.
- [ ] Error responses are consistent ProblemDetails.
- [ ] A web message resumes a waiting job and appears in its chat conversation.
