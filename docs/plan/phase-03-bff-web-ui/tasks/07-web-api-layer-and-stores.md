# T3.7 — Web API layer and Pinia stores

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | T3.3, T3.4, T3.5 | L | web |

## Goal
Build the frontend data layer with **plain Pinia setup stores and `fetch`**, and no data-fetching
libraries. Stores load through the REST API, stay fresh through SignalR, deduplicate by `seq`,
keep a bounded window of events per job, and resubscribe correctly after a reconnect.

## Files
- `src/Agentd.Web/ClientApps/shared/api/http.ts` — create: `get<T>()`, `send<T>()`, `use()` (interceptors), `ApiError`, in-memory XSRF token, `resetXsrf()`.
- `src/Agentd.Web/ClientApps/shared/api/hub.ts` — create: the `HubConnection` factory and event names.
- `src/Agentd.Web/ClientApps/dashboard/stores/session.ts` — create: `/bff/user` (pseudo-user in Mode None).
- `src/Agentd.Web/ClientApps/dashboard/stores/connection.ts` — create: hub lifecycle, `status`, subscriptions and routing.
- `src/Agentd.Web/ClientApps/dashboard/stores/jobs.ts` — create: `byId`, `active`, `waitingCount`, stats, `load`, `apply`, `cancel`, `retry`.
- `src/Agentd.Web/ClientApps/dashboard/stores/events.ts` — create: per-job windows, `open`, `close`, `append`, `loadEarlier`.
- `src/Agentd.Web/ClientApps/dashboard/stores/ui.ts` — create: theme, toasts, per-job filters, follow mode.
- `src/Agentd.Web/ClientApps/dashboard/main.ts` — modify: `createPinia`, `session.load()` then `connection.start()`.
- `src/Agentd.Web/package.json` — modify: add `@microsoft/signalr` (version pinned by the lockfile).

## Implementation
1. **`http.ts`** (see [security §2.4](../../../security/README.md#24-client-webhttpts)):
   - `credentials: 'same-origin'`;
   - `ensureToken()` fetches `/bff/antiforgery` once and keeps the token in a module variable (never
     in storage);
   - `send()` adds `X-XSRF-TOKEN`, and on `400` with `code === 'antiforgery_invalid'` it refetches
     the token once and retries;
   - `ApiError { status, code?, message }` is parsed from ProblemDetails; a 401 calls
     `session.onUnauthorized()`, which is a no-op until Phase 5.
   - **Interceptors (added 2026-10-03):** there's no axios, so `http.ts` has a small middleware
     list:
     ```ts
     export interface Middleware {
       onRequest?(req: Request): Request | Promise<Request>
       onResponse?(res: Response, req: Request): Response | Promise<Response>
       onError?(err: ApiError, req: Request): void | Promise<void>
     }
     export function use(m: Middleware): () => void   // returns an unregister function
     ```
     - Every `get`/`send` builds a `Request`, then runs each `onRequest` in registration order,
       `fetch`, each `onResponse`, and on a non-2xx, `ApiError.from(res)` → each `onError` → throw.
     - **Built-in middlewares**, registered first inside `http.ts`:
       - XSRF: adds `X-XSRF-TOKEN` to unsafe methods;
       - ProblemDetails → `ApiError`;
       - 401 → `session.onUnauthorized()`. With `CloudflareAccess` (T3.14), that's a reload, so
         Cloudflare's sign-in runs.
     - **App middlewares**, registered in `main.ts`:
       - 5xx → an error toast through the `ui` store;
       - in Development, a `console.debug` of method, URL, status and duration.
     - **Retries happen only in `http.ts`.** The one retry is the stale-XSRF recovery, which runs
       outside the middleware chain. A middleware never retries or re-sends, so a request can't
       loop.
     - **Nothing calls `fetch` directly.** An eslint `no-restricted-globals` rule for `fetch` applies
       outside `shared/api/`, so every request passes through the interceptors.
     - SignalR doesn't use `fetch`. Its equivalents are the `withUrl` options and the connection's
       `onreconnecting` / `onclose` events, in the `connection` store.
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
  - interceptors:
    - `onRequest` / `onResponse` run in registration order and can replace the request or
      response;
    - `onError` gets the parsed `ApiError` with its `code`;
    - the function `use()` returns unregisters the middleware;
    - a throwing middleware rejects the call, without retrying;
  - `events.append` drops duplicates and older `seq` values, and trims to 2,000;
  - `loadEarlier` prepends in order;
  - reconnect → `Subscribe` is called for every stream with the correct `lastSeq`;
  - an optimistic cancel rolls back on 409 and shows a toast;
  - `jobs.active` sorts waiting jobs first.

## Done when
- [ ] No data-fetching or caching library is in `package.json`; only `@microsoft/signalr` was added.
- [ ] The XSRF token lives only in memory, and stale tokens are recovered automatically.
- [ ] Every request goes through `http.ts` and its interceptors (`use()`). `fetch` isn't called
      anywhere else (eslint).
- [ ] After a reconnect the UI has no gaps or duplicates (verified by unit tests; end-to-end in T3.12).
- [ ] The stores are fully typed from the generated `types.ts`.
