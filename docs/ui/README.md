# agentd — Web UI

The Web UI shows every agent session in real time: what each agent is doing, which work item it
is on, and where it needs a human. You can also cancel, retry, or message an agent from it.
Chat (Discord, Telegram, … through messaging providers) remains the main conversation channel, and
messages sent from the UI are mirrored into all of the job's chat conversations.

- **Stack:** Vue 3 (`<script setup>`, TypeScript) · Vite · Vue Router · **Pinia (plain reactive
  stores)** · daisyUI 5 / Tailwind CSS 4 · base-ui-vue primitives · `@microsoft/signalr`
- **Look and feel:** [Design System](../design-system/README.md)
- **Backend contract:** the **BFF** (`Agentd.Bff`); see [Clean Architecture + BFF §5.1](../architect/clean-architecture-bff.md#51-agentdbff-backend-for-frontend-browser) and [Architecture §3.9](../architect/README.md). The SPA calls only `/bff/*`, `/api/*` and `/hubs/*` on its own origin.
- **Browser security:** [Security](../security/README.md): HttpOnly cookies, antiforgery, strict CSP

---

## 1. Dependency policy

There is no data-fetching, caching, virtualization or component-kit library beyond what is
listed here. State and server data live in **Pinia setup stores** that call `fetch` directly.

| Runtime dependency | Why |
|---|---|
| `vue`, `vue-router`, `pinia` | framework, routing, state |
| `@microsoft/signalr` | live event stream (the backend protocol) |
| `base-ui-vue` | accessible headless primitives for the `Ag*` components |

| Dev dependency | Why |
|---|---|
| `vite`, `@vitejs/plugin-vue`, `typescript`, `vue-tsc` | build and type-check |
| `tailwindcss`, `@tailwindcss/vite`, `daisyui` | styling (compiled away; no runtime) |
| `openapi-typescript` | generate `api/schema.d.ts` from the daemon's OpenAPI document (types only) |
| `vitest`, `@vue/test-utils` | unit tests for stores and components |

Adding anything else needs a note in this section explaining why.

---

## 2. Routes

| Path | View | Purpose |
|---|---|---|
| `/` | `DashboardView` | all active jobs, live |
| `/jobs/:id` | `SessionView` | live trace of one job (tabs: Transcript · Diff · Details) |
| `/history` | `HistoryView` | finished, failed and cancelled jobs, with search and filters |
| `/workitems/:id` | `WorkItemView` | **the whole life of one work item** across all its jobs: timeline, conversation, agent activity, PRs, plan vs actual, usage |
| `/prs` | `PullRequestsView` | **PR dashboard**: all open PRs across repos, with run review / fix now / monitor / hotfix |
| `/prs/:repo/:id` | `PullRequestView` | review runs & findings per reviewer, fix rounds, live trace |
| `/learnings` | `LearningsView` | approved learnings per repo, pending candidates, distill runs and Learnings PRs, and global-learning approval cards |
| `/repos` | `ReposView` | registered repositories: **kit status** (version, valid or invalid with errors, customized files, upgrade available), with **Initialize kit** / **Upgrade kit** buttons that open kit PRs |
| `/models` | `ModelsView` | model profiles: health (circuit breaker), concurrency in use, today's cost vs budget, outcomes per phase |
| `/setup` | *separate SPA* `ClientApps/setup` | first-run setup wizard (one-time setup link), [deployment.md §5a](../architect/deployment.md#5a-setup-ui-first-run-wizard-and-settings) |
| `/settings/*` | `SettingsView` (Admin) | Access, Azure DevOps, Git, Models, Chat, Repositories; write-only secret fields; **Health** (doctor report) |
| `/login` | `LoginView` | SSO buttons (Microsoft, Google) from `/bff/providers`; shows `access_denied` errors |
| `/settings` | `SettingsView` | read-only config summary, theme, connection info |

---

## 3. App shell

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ ● agentd        Dashboard   History   Settings          ⚠ 2 waiting   ◐ ● Live │  navbar
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  <router-view />                                                             │
│                                                                              │
└──────────────────────────────────────────────────────────────────────────────┘
                                                       ┌─────────────────────┐
                                                       │ ✓ Retry queued      │  toast (ui store)
                                                       └─────────────────────┘
```

- **Navbar:**
  - a green logo dot;
  - nav links, where the active one is underlined with `primary`;
  - a **waiting counter** (a `badge-warning` that links to the dashboard filtered to `WaitingForHuman`);
  - a theme toggle (System / Light / Dark);
  - a **connection indicator**: green "Live", amber "Reconnecting…", red "Offline".
- **Mobile:** the nav collapses into a daisyUI `drawer`.

---

## 4. Screens

### 4.1 Dashboard

```
┌ Running 3/3 ─┐ ┌ Waiting 2 ──┐ ┌ Queued 4 ──┐ ┌ Done today 7 ┐ ┌ Cost today $4.12 ┐   stats
└──────────────┘ └─────────────┘ └────────────┘ └──────────────┘ └──────────────────┘
Concurrency  ███████████░░░  3 / 3 slots                                    AgMeter

[ All | Running | Waiting | Queued | Failed ]            🔍 filter by title / WI id   AgToggleGroup

 State        Work item                       Repo     Branch                 Elapsed  Turns  Cost
 ─────────────────────────────────────────────────────────────────────────────────────────────────
 ● Running    WI-1234  Fix login redirect     agentd   ai/1234-fix-login        12m     34   $0.81
 ⚠ Waiting    WI-1240  Add audit log          api      ai/1240-audit-log        1h 3m   22   $0.55   ← row tinted warning
 ◷ Queued     WI-1251  Bump deps              web      —                        —        —    —
```

- Rows update live from the `jobs` store (state, elapsed, turns, cost). A changed cell flashes
  briefly with `bg-primary/10`.
- Clicking a row opens `/jobs/:id`. A row action menu offers Open chat (one entry per provider), Open work item,
  Open PR, Cancel and Retry.
- Waiting rows sort first and are tinted `bg-warning/10`.
- **Empty state:** "No active jobs. Tag a work item with `ai-workflow` to start one." plus a
  **Run work item…** button, which opens a modal with a WI id input and calls `POST /api/workitems/{id}/run`.

### 4.2 Session (`/jobs/:id`)

Below the header, a **phase stepper** (Design → Plan → Implement → Test → Review) shows the current
phase, loop counts (e.g. `Test ↺2`) and each phase's model profile and cost. An open gate shows
**Approve / Reject** buttons. A fourth tab, **Artifacts**, shows `design.md`, `plan.md`, the test
report, `review.md` and the retrospective. See
[workflow-and-learning.md](../architect/workflow-and-learning.md).

```
← Dashboard
WI-1234 · Fix login redirect                      ● Running   ⏱ 12m   34 turns   $0.81
agentd · ai/1234-fix-login · session 8f3c…      [Discord ↗][Telegram ↗] [Work item ↗] [PR ↗]   [Cancel] [Retry]

[ Transcript | Diff | Details ]                                                     AgTabs
┌──────────────────────────────────────────────────────────────┬───────────────────────────┐
│ Filters: [Text][Tools][Questions][State][Errors]  ⤓ Follow   │ Timeline                  │
│ ───────────────── ◷ Queued → ● Running 10:02 ─────────────── │ 10:01 Queued              │
│ Claude  I'll start by reproducing the redirect bug…          │ 10:02 Running             │
│ ▸ Bash  npm test -- login.spec.ts          ✗ exit 1   1.2s   │ 10:09 Waiting ⚠           │
│ ▸ Read  src/auth/redirect.ts                        0.1s     │ 10:14 Running             │
│ ▾ Edit  src/auth/redirect.ts                        0.2s     │                           │
│   │ - return res.redirect(req.query.next)                    │ Turns   34 / 200          │
│   │ + return res.redirect(safeNext(req.query.next))          │ ██████░░░░░░░░░░          │
│ ⚠ Question  Keep the legacy /login endpoint?                 │ Tokens  412k              │
│                            No, remove it. — tngo (Telegram) ▸│ Cost    $0.81             │
│ ─ 34 turns · 412k tokens · $0.81 ─                           │                           │
│                              [ Load earlier ]  (top)         │                           │
├──────────────────────────────────────────────────────────────┴───────────────────────────┤
│ 💬 Message the agent… (mirrored to Discord + Telegram)                            [Send] │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

**Transcript tab**

- **Event rendering** follows the design-system map (§2.4). Tool calls use `AgCollapsible`
  (collapsed by default, except failed tool results and Edit/Write, which show an inline diff).
- **Filters:** an `AgToggleGroup` of event categories. The selection is stored in the `ui` store,
  per job.
- **Follow mode:**
  - it is on by default, and the pane auto-scrolls to new events;
  - scrolling up turns it off and shows a floating **"N new events ↓"** button;
  - clicking that button or pressing `End` turns it back on.
- **Windowing** (no virtual-list library):
  - the store keeps and renders at most the latest **500** events for the job;
  - **Load earlier** fetches `?before=<oldestSeq>&limit=200` and prepends the results;
  - when the list grows past 2000 events, the oldest are dropped from the *rendered* set and
    "Load earlier" brings them back;
  - large tool outputs are truncated to 200 lines, with a **Show all** link.
- **Composer:** enabled in `WaitingForHuman` and `Running`. When the job is running, the message
  is queued as the next turn, and the composer says so.

**Diff tab**

- Loads `GET /api/jobs/{id}/diff` when the tab opens. It reloads (debounced to 2s) whenever an
  `Edit`/`Write` `tool.result` arrives while the tab is visible.
- A file list on the left and a unified diff on the right. Diffs are parsed by `utils/diff.ts`
  (a small in-house unified-diff parser) and rendered by `DiffView`.

**Details tab**

- Work item fields (the description is rendered as sanitized HTML), repo, worktree path, branch,
  session ID, attempts, last error, and links.

### 4.3 History

- A table like the dashboard's, loaded with **server-side paging** (`GET /api/history?page=`).
  Filters: state, repo, date range, and a free-text search.
- A row opens the same `SessionView`. With no live events, it runs as a replay using the same
  components.

- Rows can be **grouped by work item**; the work item cell links to `WorkItemView`.

### 4.3a Work item (`/workitems/:id`)

A work item often spans several jobs: a first run, a rework, review fix rounds, the knowledge hand-off
and the close-out (Phase 2b). This view tells its **whole story on one page**, and it keeps working
after the chat thread is deleted, because the history lives in agentd's database.

- **Header:** the work item (link to Azure DevOps), repository, current state and phase, the PRs
  (code PR, knowledge sync PR) with their status, total elapsed time and usage.
- **Tabs:**
  - **Timeline:** every lifecycle step and message in order, with each **job as a section**
    ("Run 1", "Rework", "Hand-off"). Steps include claimed, worktree ready, each `set_phase` summary,
    plan and approval, questions and answers, pushes, PR opened, review comments and fix rounds,
    ready to complete, merged, hand-off proposal and agreement, close-out.
  - **Conversation:** the chat exactly as it happened, **both directions**: what agentd and the agent
    posted (from the outbox, with delivery status), your replies and commands, and mirrored messages.
    A composer sends a message to the active job (`SubmitDeveloperMessage`).
  - **Activity:** the agent's turns, with collapsible tool calls (`ToolCallCard`), and a link to the
    raw transcript.
  - **PRs:** each PR with its review threads and the fix round that addressed them.
  - **Plan & usage:** the plan with its estimate against actual (time, share of the 5-hour window),
    and 5-hour and weekly usage over the work item's life.
- **Live:** while a job of the work item is active, new entries stream in via the hub (subscribing to
  each of its jobs).

### 4.4 Pull requests

**Dashboard (`/prs`)**

```
┌ Open PRs 14 ┐ ┌ Needs attention 4 ┐ ┌ CI failing 2 ┐ ┌ Conflicts 1 ┐ ┌ Fixing now 1 ┐
[ All | agentd | Mine | Needs attention | CI failing | Conflicts ]     Repo: [all ▾]   🔍

 Repo    PR     Title                       Author   Target   CI   Votes     Threads  Merge   agentd
 ───────────────────────────────────────────────────────────────────────────────────────────────────────
 agentd  #123   Fix login redirect (WI-1234) agentd  main     ✗    ⏳ 1/2    3 active  ✓      ● Fixing r2   [⋯]
 api     #88    Add audit log                tngo    main     ✓    ✓ 2/2    0         ⚠      👁 Monitored   [⋯]
 web     #41    Bump vite                    agentd  main     ✓    ⏳ 0/1    1 active  ✓      ✓ Reviewed    [⋯]
```

- The data comes from the `pullRequests` store (`GET /api/prs`), with live updates from `pr.*` events.
- **Needs attention** = CI failing, conflicts, "waiting for author" votes, or active threads older
  than a day.
- The row menu (`[⋯]`), for Operators: **Run review…**, **Fix now…**, **Monitor on/off**,
  **Create hotfix…**, **Open in Azure DevOps ↗**.
- **Run review** dialog (`AgModal`):
  - a checkbox list of the repo's **predefined reviewers**, from `GET /api/repos/{repo}/reviewers`
    (the kit's `.agentd/reviewers/*.md`), each showing its title, description and `appliesTo`;
  - `defaultReviewers` are preselected, and reviewers whose paths don't match the PR are greyed
    out, with the reason;
  - a toggle to **Post to PR** or **Dry run (UI only)**;
  - an optional model override, limited to the allowed profiles.
- **Fix now** dialog: an optional instruction ("make the build green; don't touch the API"), then
  `POST …/fix`.

**PR detail (`/prs/:repo/:id`)**

- **Header:** title, branches, CI, votes, merge status, monitoring toggle, and links.
- **Tabs:**
  - **Reviews:** runs by head commit, with findings grouped by reviewer and severity, each linked
    to its ADO thread and status;
  - **Fix rounds:** a timeline of triage → commits → replies, per round;
  - **Trace:** the same `EventList` as the session view, for the active review or fix job;
  - **Threads:** active and resolved, with who triggered what.

### 4.5 Settings

- Theme: System / Light / Dark (`AgToggleGroup`).
- Connection: hub URL, state, last event `seq`, and a reconnect button.
- Read-only daemon config summary (`GET /api/config`, with secrets redacted): the tag, poll
  interval, max concurrency and repositories.

---

## 5. State: Pinia stores

All stores are **setup stores** (`defineStore('x', () => { ... })`) built from `ref`, `computed`
and plain async functions. There is no caching layer. A store *is* the cache, and it is kept fresh
by SignalR.

```
          REST (fetch)                         SignalR hub
              │                                    │
              ▼                                    ▼
   ┌──────────────────┐   events   ┌───────────────────────┐
   │ jobs store       │◀──────────▶│ connection store      │
   │ jobs by id       │            │ HubConnection, status │
   └──────────────────┘            │ routes events by type │
   ┌──────────────────┐   events   │                       │
   │ events store     │◀──────────▶│                       │
   │ per-job windows  │            └───────────────────────┘
   └──────────────────┘
   ┌──────────────────┐
   │ ui store         │ theme, toasts, filters, follow mode
   └──────────────────┘
```

### 5.1 `connection`

```ts
export const useConnectionStore = defineStore('connection', () => {
  const status = ref<'connecting' | 'live' | 'reconnecting' | 'offline'>('connecting')
  const lastSeq = ref(0)
  let hub: HubConnection | null = null
  const subscriptions = new Set<string>()                 // 'all' or job ids

  async function start() {
    hub = new HubConnectionBuilder().withUrl('/hubs/events').withAutomaticReconnect().build()
    hub.on('event', (e: AgentEvent) => route(e))
    hub.onreconnecting(() => (status.value = 'reconnecting'))
    hub.onreconnected(async () => { status.value = 'live'; await resubscribeAll() })
    hub.onclose(() => (status.value = 'offline'))
    await hub.start(); status.value = 'live'
    await subscribe('all', lastSeq.value)
  }

  function route(e: AgentEvent) {
    // No global dedupe here: a per-job subscription replays events older than what the 'all'
    // stream has already delivered. Each consumer dedupes by seq against its own high-water mark.
    lastSeq.value = Math.max(lastSeq.value, e.seq)          // resume point for the 'all' stream
    useJobsStore().apply(e)                                 // skips e.seq <= job.lastSeq
    useEventsStore().append(e)                              // skips unless window loaded and e.seq > newestSeq
  }
  // subscribe(jobId, afterSeq) / unsubscribe / resubscribeAll ...
  return { status, lastSeq, start, subscribe, unsubscribe }
})
```

- The dashboard subscribes to `all`, which receives only summary events: `job.*` and `turn.result`.
  A session view additionally subscribes to its job, which receives the full event stream.
- After a reconnect, every subscription is re-sent with the latest `seq` the store has for it, and
  the hub replays anything missed.

### 5.2 `jobs`

```ts
export const useJobsStore = defineStore('jobs', () => {
  const byId = reactive(new Map<number, JobSummary>())
  const loading = ref(false)
  const error = ref<string | null>(null)

  const active = computed(() => [...byId.values()].filter(j => !isFinal(j.state)).sort(byWaitingFirst))
  const waitingCount = computed(() => active.value.filter(j => j.state === 'WaitingForHuman').length)

  async function load() { /* GET /api/dashboard → fill byId + stats (one screen-shaped BFF call) */ }
  function apply(e: AgentEvent) { /* job.state_changed / turn.result → patch byId.get(e.jobId) */ }
  async function cancel(id: number) { /* POST, optimistic state, rollback + toast on error */ }
  async function retry(id: number) { /* ... */ }
  return { byId, loading, error, active, waitingCount, load, apply, cancel, retry }
})
```

### 5.3 `events`

```ts
interface JobWindow { events: AgentEvent[]; oldestSeq: number; newestSeq: number; hasMore: boolean }

export const useEventsStore = defineStore('events', () => {
  const windows = reactive(new Map<number, JobWindow>())

  async function open(jobId: number) { /* GET /api/jobs/{id}/events?limit=500 → window; connection.subscribe(jobId, newestSeq) */ }
  function close(jobId: number) { /* unsubscribe; drop window after 5 min of not being viewed */ }
  function append(e: AgentEvent) { /* ignore if no window or e.seq <= newestSeq (dedupe); push; trim to 2000 */ }
  async function loadEarlier(jobId: number) { /* GET ?before=oldestSeq&limit=200 → unshift */ }
  return { windows, open, close, append, loadEarlier }
})
```

### 5.4 `session`

This store holds the current user from `GET /bff/user` (`name`, `roles`, `provider`), or `null` on 401. It exposes
`login(provider)`, which navigates to `/bff/login?provider=microsoft|google&returnUrl=…`, a `hasRole()` helper for showing or hiding
Operator and Admin actions (the server enforces them regardless), and `logout()`, which sends `POST /bff/logout` and then
calls `resetXsrf()`. In `Auth: None` mode, `/bff/user` returns a local pseudo-user.

### 5.5 `ui`

This store holds the theme (persisted in `localStorage` and applied to `<html data-theme>`), the
toast queue, per-job event filters, and the follow mode flag per job.

---

## 6. API layer (`src/Agentd.Web/ClientApps/shared/api/`)

| File | Content |
|---|---|
| `schema.d.ts` | generated by `openapi-typescript` from `/openapi/v1.json` (`npm run gen:api`) |
| `types.ts` | friendly aliases: `JobSummary`, `JobDetail`, `AgentEvent`, `JobState` |
| `http.ts` | `get<T>()` and `send<T>()` over `fetch`. Sends `credentials: 'same-origin'` (HttpOnly cookie auth) and adds the in-memory **antiforgery token** as `X-XSRF-TOKEN` on unsafe methods, refetching it once if it is stale. Parses JSON and throws an `ApiError { status, message }`. See [Security §2.4](../security/README.md#24-client-webhttpts). |
| `hub.ts` | the SignalR connection factory and event names |

Errors surface as a daisyUI `alert` in the affected view, plus a toast for failed actions. A `401`
sends the browser to the `/login` page with `returnUrl=<current path>`, which offers **Sign in with Microsoft / Google** (links to `/bff/login?provider=…`; see [authentication.md §7](../security/authentication.md#7-web-ui)).

---

## 7. Components

**Reusable (`components/ui/`, `Ag*`)**: see the [Design System §6](../design-system/README.md#6-component-layer).

**Feature (`components/`)**

| Component | Purpose |
|---|---|
| `JobTable` | the dashboard and history table; columns configurable |
| `StateBadge` | job state → the badge, icon and label from the design system |
| `StatsBar` | the dashboard `stat` tiles + concurrency `AgMeter` |
| `EventList` | windowed transcript: follow mode, "N new", load earlier |
| `EventItem` | a switch on `event.type` → the specific renderer below |
| `AssistantText` | markdown-lite rendering (paragraphs, code spans and blocks); no raw HTML |
| `ToolCallCard` | `AgCollapsible` holding the tool name, args summary, status, duration and output |
| `QuestionBubble` / `ReplyBubble` | ask_developer and developer replies (daisyUI `chat`) |
| `StateDivider` | state change line |
| `TurnSummary` | turns · tokens · cost line |
| `DiffView` | a file list plus a unified diff with line numbers |
| `MessageComposer` | textarea + Send (`Ctrl+Enter`) |
| `ConnectionIndicator` | reflects the `connection.status` |

---

## 8. Keyboard shortcuts

| Key | Action |
|---|---|
| `g d` / `g h` | go to Dashboard / History |
| `j` / `k` | next / previous job row (Dashboard) |
| `Enter` | open the selected job |
| `1` `2` `3` | Transcript / Diff / Details tab |
| `End` | resume follow mode |
| `Ctrl+Enter` | send message |
| `?` | show shortcuts (modal) |

---

## 9. Security in the UI

The full policy is in **[docs/security](../security/README.md)**. The UI-facing rules:

- **Strict CSP** (`script-src 'self'`, `style-src 'self'`, no inline, no eval). The HTML shell is the
  Razor layout in `Agentd.Web` (`Views/Shared/_Layout.cshtml`). It contains no inline script or style, and
  the pre-paint theme loader is `public/theme-init.js`.
  Use the runtime-only Vue build only.
- **HttpOnly cookies only.** The antiforgery request token lives in memory (`http.ts`), and
  `resetXsrf()` is called after login and logout.
- **Login** is SSO (Microsoft or Google) through links to `/bff/login?provider=…` (GET), not a form POST, because of `form-action 'self'`. No IdP JavaScript is loaded.
- Agent output is **untrusted**. Render it as text or through the markdown-lite renderer, never
  with `v-html`. The work item description is the one exception: it is sanitized server-side
  before it is sent.
- External links (work item, PR, chat conversations) use `rel="noopener noreferrer"` and open in a new tab.
- The UI never stores tokens. Auth is a same-origin HTTP-only cookie.

---

## 10. Folder structure

```
src/Agentd.Web/                     # Razor class library + all Vue apps (one package.json)
├── Agentd.Web.csproj               # Razor SDK; -p:BuildWeb=true runs npm ci + build before dotnet build
├── Views/
│   ├── Shared/_Layout.cshtml       # <head>: theme-init + @Vite.Tags(app); no inline code (CSP)
│   └── Spa/dashboard.cshtml        # one view per app: sets ViewData["App"], renders <div id="app">
├── Controllers/SpaController.cs    # one action per app; the dashboard is "/" + client-route fallback
├── Vite/ViteHelper.cs              # reads/caches manifest.json, renders tags; dev-server mode in Development
├── WebHosting.cs                   # AddWebHosting / UseWebHosting (dev proxy of /_content/Agentd.Web → Vite)
├── package.json, vite.config.ts    # Vite backend integration; every ClientApps/<app>/main.ts is an entry
├── public/theme-init.js            # sets data-theme before first paint (external file, CSP-safe)
├── ClientApps/
│   ├── shared/                     # used by several apps
│   │   ├── styles/app.css          # Tailwind + daisyUI + agentd themes
│   │   ├── api/                    # http.ts, hub.ts, types.ts, schema.d.ts (generated)
│   │   ├── components/ui/          # Ag* reusable components (base-ui-vue + daisyUI)
│   │   ├── components/icons/       # inline SVG SFCs
│   │   └── utils/                  # diff.ts, format.ts (durations, cost), markdown-lite.ts
│   └── dashboard/                  # the agentd UI
│       ├── main.ts                 # createApp, pinia, router, connection.start()
│       ├── App.vue, router.ts
│       ├── stores/                 # session.ts, connection.ts, jobs.ts, events.ts, pullRequests.ts, ui.ts
│       ├── components/             # feature components (§7)
│       └── views/                  # DashboardView, SessionView, HistoryView, PullRequestsView, …, SettingsView
├── tests/                          # vitest: stores (event dedupe, windowing, reconnect), components
└── wwwroot/                        # Vite build output, served under /_content/Agentd.Web/ (git-ignored)
```

**Adding another SPA:** create `ClientApps/<app>/main.ts`, add `Views/Spa/<app>.cshtml` (setting
`ViewData["App"] = "<app>"`) and an action on `SpaController` with its route. Vite picks the new entry
up automatically, and `ViteHelper.Tags("<app>")` renders it.

## 11. Testing focus

- `events` store: dedupe by `seq`, the trimming window, and load-earlier merging.
- `connection` store: resubscribe with the correct `afterSeq` after a reconnect (hub mocked).
- `jobs` store: optimistic cancel and rollback.
- `diff.ts` parser: additions, deletions, renames, binary files.
