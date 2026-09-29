# T3.7 — Web API layer and Pinia stores

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | T3.3, T3.4, T3.5 | L | web |

## Goal
Build the frontend data layer with **plain Pinia setup stores and `fetch`**, and no data-fetching
libraries. Stores load through the REST API, stay fresh through SignalR, deduplicate by `seq`,
keep a bounded window of events per job, and resubscribe correctly after a reconnect.

## Files
- `web/src/api/http.ts` — create: `get<T>()`, `send<T>()`, `ApiError`, in-memory XSRF token, `resetXsrf()`.
- `web/src/api/hub.ts` — create: the `HubConnection` factory and event names.
- `web/src/stores/session.ts` — create: `/bff/user` (pseudo-user in Mode None).
- `web/src/stores/connection.ts` — create: hub lifecycle, `status`, subscriptions and routing.
- `web/src/stores/jobs.ts` — create: `byId`, `active`, `waitingCount`, stats, `load`, `apply`, `cancel`, `retry`.
- `web/src/stores/events.ts` — create: per-job windows, `open`, `close`, `append`, `loadEarlier`.
- `web/src/stores/ui.ts` — create: theme, toasts, per-job filters, follow mode.
- `web/src/main.ts` — modify: `createPinia`, `session.load()` then `connection.start()`.
- `web/package.json` — modify: add `@microsoft/signalr` (version pinned by the lockfile).

## Implementation
1. **`http.ts`** (see [security §2.4](../../../security/README.md#24-client-webhttpts)):
   - `credentials: 'same-origin'`;
   - `ensureToken()` fetches `/bff/antiforgery` once and keeps the token in a module variable (never
     in storage);
   - `send()` adds `X-XSRF-TOKEN`, and on `400` with `code === 'antiforgery_invalid'` it refetches
     the token once and retries;
   - `ApiError { status, code?, message }` is parsed from ProblemDetails; a 401 calls
     `session.onUnauthorized()`, which is a no-op until Phase 5.
2. **`connection` store:**
   - `new HubConnectionBuilder().withUrl('/hubs/events').withAutomaticReconnect().build()`;
   - `status` is `connecting | live | reconnecting | offline`;
   - `subscriptions: Map<string, number /* lastSeq */>`;
   - `on('event', route)`;
   - `onreconnected` → resubscribe every entry with its own `lastSeq`;
   - `onclose` → `offline`, retrying `start()` with backoff (1, 2, 5, 10, 30 s).
3. **Routing:** `route(e)` updates `subscriptions.get(key)` to the max `seq`, then calls
   `jobs.apply(e)` (summary fields) and `events.append(e)` (full events, only when a window is open).
   Each consumer deduplicates against its own high-water mark. There is **no** global dedupe
   (see [UI §5.1](../../../ui/README.md#51-connection)).
4. **`jobs` store:**
   - `reactive(new Map<number, JobSummary>())`, `load()` from `/api/dashboard` (fills `byId` and
     `stats`);
   - `apply()` handles `job.state_changed`, `job.created`, `turn.result` and `job.waiting`;
   - `cancel` and `retry` are **optimistic**: set a pending flag, then roll back and show a toast on
     `ApiError`;
   - computed values: `active` (waiting first, then by `startedAt`) and `waitingCount`.
5. **`events` store:**
   - `JobWindow { events: AgentEvent[]; oldestSeq; newestSeq; hasMore; lastViewedAt }`;
   - `open(jobId)` → `GET /api/jobs/{id}/events?limit=500`, then `connection.subscribe(String(jobId), newestSeq)`;
   - `append` ignores `seq ≤ newestSeq`, pushes, and trims to **2,000 rendered** events by dropping
     the oldest (setting `hasMore = true`);
   - `loadEarlier(jobId)` → `?before=oldestSeq&limit=200` → `unshift`;
   - `close(jobId)` unsubscribes and drops the window after 5 minutes unless it is reopened.
6. **`ui` store:**
   - `theme: 'system' | 'agentd' | 'agentd-dark'`, persisted in `localStorage` inside `try/catch`,
     and applied to `document.documentElement.dataset.theme`;
   - the `toasts` queue;
   - `filters: Map<jobId, Set<EventCategory>>` and `follow: Map<jobId, boolean>`.
7. `main.ts` calls `connection.start()` and then `connection.subscribe('all', 0)` once the dashboard
   has loaded (using its `newestSeq` from `/api/dashboard`, if provided). Otherwise it uses `0`,
   which is cheap because the `all` stream is summary-only.

## Tests
- vitest with a mocked `fetch` and a fake hub (an `EventTarget`-based stub injected through `hub.ts`):
  - `http.send` retries exactly once on `antiforgery_invalid`, and never stores the token in
    `localStorage` or `sessionStorage`;
  - `events.append` drops duplicates and older `seq` values, and trims to 2,000;
  - `loadEarlier` prepends in order;
  - reconnect → `Subscribe` is called for every stream with the correct `lastSeq`;
  - an optimistic cancel rolls back on 409 and shows a toast;
  - `jobs.active` sorts waiting jobs first.

## Done when
- [ ] No data-fetching or caching library is in `package.json`; only `@microsoft/signalr` was added.
- [ ] The XSRF token lives only in memory, and stale tokens are recovered automatically.
- [ ] After a reconnect the UI has no gaps or duplicates (verified by unit tests; end-to-end in T3.12).
- [ ] The stores are fully typed from the generated `types.ts`.
