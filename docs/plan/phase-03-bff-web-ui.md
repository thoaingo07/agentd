# Phase 3 — BFF + Web UI v1 + browser security

**Goal:** watch every session live in the browser. The BFF serves screen-shaped APIs and a SignalR
event stream, the Vue app shows the dashboard and the session trace, and the browser security
model (HttpOnly cookies, antiforgery, strict CSP) is in place from the first screen.

Design refs: [UI spec](../ui/README.md) · [Design System](../design-system/README.md) ·
[Web Security](../security/README.md) · [Clean Architecture + BFF §5.1](../architect/clean-architecture-bff.md#51-agentdbff-backend-for-frontend-browser)

---

## Scope

**In**

- **Event bus:**
  - `IEventStore` (append, then fan out) with `(job_id, seq)` paging in both directions;
  - `IEventPublisher` (channels + PostgreSQL `NOTIFY`);
  - monthly partitions on `events`.
- **BFF (`Agentd.Bff`):**
  - `/api/dashboard`, `/api/jobs/{id}`, `/api/jobs/{id}/events?after|before`, `/api/jobs/{id}/diff`,
    `/api/history`;
  - `POST /api/jobs/{id}/cancel|retry|messages`, `POST /api/workitems/{id}/run`;
  - `/bff/antiforgery` and `/bff/user` (a pseudo-user in `Mode: None`);
  - the OpenAPI document.
- **SignalR hub** `/hubs/events`: `Subscribe(jobId|all, afterSeq)` with replay, read-only, with an
  Origin check.
- **Security:**
  - the `ValidateAntiforgeryFilter` on `/api` and `/bff`;
  - HttpOnly `__Host-` cookies;
  - `UseSecurityHeaders` (strict CSP, including Trusted Types in **report-only** mode);
  - `/api/csp-report`;
  - loopback-only enforcement while `Auth.Mode = None`.
- **Web UI:**
  - Pinia stores `session`, `connection`, `jobs`, `events` and `ui`;
  - `http.ts` (in-memory XSRF token) and `hub.ts`;
  - generated API types;
  - views: **Dashboard**, **Session** (Transcript + Diff + Details tabs), **History** and **Settings**;
  - `Ag*` components: Button, Collapsible, Tabs, Tooltip, ToggleGroup, Meter, Modal, Toast;
  - feature components: `EventList` (windowing, follow mode, load earlier), `ToolCallCard`,
    `StateBadge`, `DiffView` (with an in-house diff parser) and `MessageComposer`;
  - web messages are mirrored to chat through `SubmitDeveloperMessage`.
- **Playwright smoke tests:** the pages load with **zero CSP violations**, and unsafe requests
  without the header return 400.

**Out:** the phase stepper and artifacts (Phase 4), login and roles (Phase 5), PR screens (Phase 7).

---

## Tasks

1. Event store and publisher, including a `NOTIFY` listener; paging tests.
2. BFF endpoints + view models + OpenAPI; `openapi-typescript` generation (`npm run gen:api`).
3. The SignalR hub with replay-from-`seq` and the Origin allowlist.
4. Security middleware:
   - antiforgery filter + `/bff/antiforgery`;
   - security headers + CSP;
   - the CSP report endpoint (rate-limited);
   - startup refuses a non-loopback binding in `Mode: None`.
5. Web stores + API layer (dedupe by `seq`, reconnect → resubscribe).
6. `Ag*` components on base-ui-vue, styled with the daisyUI theme.
7. Dashboard + StatsBar + JobTable.
8. Session view: EventList (500-event window, load earlier, follow mode, "N new"), ToolCallCard,
   DiffView, MessageComposer.
9. History with server-side paging.
10. Playwright CSP + antiforgery smoke tests in CI; `Bff.Tests` with `WebApplicationFactory`.

## Exit criteria (the demo)

- With two jobs running, the dashboard updates live (state, turns, cost). Opening a job streams its
  transcript live, with collapsible tool calls, and the diff tab shows the branch diff.
- Killing and restarting agentd: the UI shows "Reconnecting…" and then resumes with **no missing or
  duplicated events**.
- A message sent from the UI resumes a waiting job and appears in the Discord/Telegram thread.
- Playwright: no CSP violations on any page; `POST` without `X-XSRF-TOKEN` → 400; no JS-readable
  cookies.
- Lighthouse accessibility score ≥ 95 in both themes.

## Risks / open questions for review

- **base-ui-vue maturity:** if a primitive is missing or buggy, fall back to daisyUI + native
  elements for that component. Is that acceptable?
- **Trusted Types:** stays report-only until Phase 10 confirms Vue, base-ui-vue and SignalR raise no
  violations.
- **Diff rendering:** is the in-house parser enough, or do we want side-by-side view in v1?
