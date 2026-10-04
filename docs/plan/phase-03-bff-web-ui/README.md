# Phase 3 — BFF + Web UI v1 + browser security

**Goal:** watch every session live in the browser. The BFF serves screen-shaped APIs and a SignalR
event stream, the Vue app shows the dashboard and the session trace, and the browser security
model (HttpOnly cookies, antiforgery, strict CSP) is in place from the first screen.

Design refs: [UI spec](../../ui/README.md) · [Design System](../../design-system/README.md) ·
[Web Security](../../security/README.md) · [Clean Architecture + BFF §5.1](../../architect/clean-architecture-bff.md#51-agentdbff-backend-for-frontend-browser)

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
  - views: **Dashboard**, **Session** (Transcript + Diff + Details tabs), **History**, **Work item**
    (added 2026-10-03, below) and **Settings**;
  - `Ag*` components: Button, Collapsible, Tabs, Tooltip, ToggleGroup, Meter, Modal, Toast;
  - feature components: `EventList` (windowing, follow mode, load earlier), `ToolCallCard`,
    `StateBadge`, `DiffView` (with an in-house diff parser) and `MessageComposer`;
  - web messages are mirrored to chat through `SubmitDeveloperMessage`.
- **Playwright smoke tests:** the pages load with **zero CSP violations**, and unsafe requests
  without the header return 400.

- **Work item view (added 2026-10-03):**
  - one page per work item across all its jobs: timeline, conversation in both directions, agent
    activity, PRs and review rounds, plan against actual, usage ([UI §4.3a](../../ui/README.md));
  - **BFF:** `GET /api/workitems/{id}` (summary + jobs + PRs) and `GET /api/workitems/{id}/timeline?after|before`
    (events of all its jobs plus the conversation: outbox and inbound, merged in time order);
  - chat commands are recorded as events (`chat.command`: name, args, user), so the conversation is
    complete; today only the outcome is kept;
  - Session and History link to it. The Session view's Details tab shows the Phase 2b fields: phase,
    plan status and estimate, fix rounds, hand-off status.

- **Publishing on the internet (added 2026-10-03):** the Web UI is served by the daemon on the same
  server. To reach it from elsewhere before Phase 5 SSO: an SSH tunnel, or **Cloudflare Tunnel +
  Cloudflare Access**, with agentd verifying the Access token (T3.14). A request that came through a
  proxy never counts as a local request.

**Out:** the phase stepper and artifacts (Phase 4), login and roles (Phase 5), PR screens (Phase 7).

---

## Tasks

Detailed tasks: [tasks/README.md](tasks/README.md)

| ID | Task | Depends on | Size | Status |
|---|---|---|---|---|
| T3.1 | [Event store and live publisher](tasks/01-event-store-and-publisher.md) | Phase 1 (`events` table, `RecordAgentOutput`) | L | ☑ |
| T3.2 | [BFF endpoints and view models](tasks/02-bff-endpoints-and-view-models.md) | T3.1, Phase 2 (`SubmitDeveloperMessage`) | M | ☑ |
| T3.3 | [OpenAPI document and generated TypeScript types](tasks/03-openapi-and-ts-types.md) | T3.2 | S | ☑ |
| T3.4 | [SignalR events hub with replay](tasks/04-signalr-events-hub.md) | T3.1 | M | ☑ |
| T3.5 | [Antiforgery with HttpOnly cookies](tasks/05-antiforgery-and-cookies.md) | T3.2 | M | ☑ |
| T3.6 | [Security headers, strict CSP and CSP reporting](tasks/06-security-headers-and-csp.md) | T3.2, Phase 0 (web build in `wwwroot`) | M | ☑ |
| T3.7 | [Web API layer and Pinia stores](tasks/07-web-api-layer-and-stores.md) | T3.3, T3.4, T3.5 | L | ☑ |
| T3.8 | [Reusable `Ag*` components (base-ui-vue + daisyUI)](tasks/08-ag-components.md) | Phase 0 (theme, web scaffold) | M | ☑ |
| T3.9 | [App shell and Dashboard view](tasks/09-dashboard-view.md) | T3.7, T3.8 | M | ☐ |
| T3.10 | [Session view: transcript, diff, details, composer](tasks/10-session-view.md) | T3.7, T3.8, T3.9 | L | ☐ |
| T3.11 | [History view with server-side paging](tasks/11-history-view.md) | T3.2, T3.9, T3.10 | S | ☐ |
| T3.12 | [Playwright smoke tests and BFF security test suite](tasks/12-e2e-and-bff-security-tests.md) | T3.4, T3.5, T3.6, T3.9, T3.10, T3.11 | M | ☐ |
| T3.13 | [Work item view: one timeline across jobs](tasks/13-work-item-view.md) | T3.2, T3.4, T3.7, T3.10 | M | ☐ |
| T3.14 | [Cloudflare Access sign-in mode](tasks/14-cloudflare-access-auth.md) | T3.5 | S | ☑ |

## Exit criteria (the demo)

- With two jobs running, the dashboard updates live (state, turns, cost). Opening a job streams its
  transcript live, with collapsible tool calls, and the diff tab shows the branch diff.
- Killing and restarting agentd: the UI shows "Reconnecting…" and then resumes with **no missing or
  duplicated events**.
- A message sent from the UI resumes a waiting job and appears in the Discord/Telegram thread.
- Playwright: no CSP violations on any page; `POST` without `X-XSRF-TOKEN` → 400; no JS-readable
  cookies.
- Lighthouse accessibility score ≥ 95 in both themes.
- Opening work item #5613 (three jobs, two PRs, a hand-off and a deleted thread) shows its whole story
  on one page: every step in the timeline, the full conversation in both directions, and the plan
  against actual.

## Risks / open questions for review

- **base-ui-vue maturity:** if a primitive is missing or buggy, fall back to daisyUI + native
  elements for that component. Is that acceptable?
- **Trusted Types:** stays report-only until Phase 10 confirms Vue, base-ui-vue and SignalR raise no
  violations.
- **Diff rendering:** is the in-house parser enough, or do we want side-by-side view in v1?
