# T3.9 — App shell and Dashboard view

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | T3.7, T3.8 | M | web |

## Goal
Turn the Phase 0 empty shell into the real app frame:
- a navbar with the waiting counter and the connection indicator;
- routes;
- the **Dashboard**, a live table of active jobs with stat tiles, a concurrency meter, filters and
  the **Run work item…** action.

## Files
- `src/Agentd.Web/ClientApps/dashboard/router.ts` — modify: routes `/`, `/jobs/:id`, `/history`, `/settings`, and a 404.
- `src/Agentd.Web/ClientApps/dashboard/App.vue` — modify: navbar, `<router-view>`, `AgToastHost`, `TooltipProvider`; mobile drawer.
- `src/Agentd.Web/ClientApps/dashboard/components/ConnectionIndicator.vue` — create.
- `src/Agentd.Web/ClientApps/dashboard/components/StatsBar.vue` — create: daisyUI `stats` + `AgMeter`.
- `src/Agentd.Web/ClientApps/dashboard/components/JobTable.vue` — create: reused by History (T3.11).
- `src/Agentd.Web/ClientApps/dashboard/components/RunWorkItemModal.vue` — create.
- `src/Agentd.Web/ClientApps/dashboard/views/DashboardView.vue` — create.
- `src/Agentd.Web/ClientApps/dashboard/views/SettingsView.vue` — create: theme, connection info, read-only config summary.
- `src/Agentd.Bff/Endpoints/ConfigEndpoints.cs` — create: `GET /api/config` (redacted summary).

## Implementation
1. **Navbar:**
   - a green logo dot and "agentd";
   - links Dashboard / History / Settings, with the active one underlined in `primary`;
   - a **waiting counter** (`badge-warning`, `jobs.waitingCount`) linking to `/?filter=waiting`;
   - the theme toggle (System / Light / Dark);
   - `ConnectionIndicator`: green "Live", amber "Reconnecting…", red "Offline", with an
     `aria-live="polite"` label.
2. **Mobile:** below `md` the navbar collapses into a daisyUI `drawer`. The layout never scrolls
   horizontally, and tables scroll inside their container.
3. **`StatsBar`:** tiles for Running (x/max), Waiting, Queued, Done today and Cost today (from
   `jobs.stats`), plus `AgMeter` for concurrency. Numbers use `tabular-nums`.
4. **`JobTable`** props: `jobs`, `columns`, `rowAction`.
   - Columns: state (`AgStateBadge`), work item (`WI-1234` + title), repo, branch (mono), elapsed,
     turns and cost.
   - Waiting rows are tinted `bg-warning/10`.
   - A changed cell flashes `bg-primary/10` for about 600 ms, respecting reduced motion.
   - Rows are keyboard navigable (`j` / `k`, Enter opens), and a click navigates to `/jobs/:id`.
   - Row menu: Open work item ↗, Open chat ↗ (one entry per conversation link), Cancel and Retry
     (through the `jobs` store).
5. **Filters:** an `AgToggleGroup` with All / Running / Waiting / Queued / Failed, synced to the
   `?filter=` query. A text search filters by title or `WI-id` on the client.
6. **Empty state:** "No active jobs. Tag a work item with `ai-workflow` to start one." plus
   **Run work item…** → `RunWorkItemModal` (a WI ID input, validated as a positive integer) →
   `POST /api/workitems/{id}/run` → a toast and navigation to the new job.
7. **Live updates:** the view reads `jobs.active`. There is no polling; updates arrive through the
   `connection` store.
8. **Settings:**
   - the theme toggle;
   - connection details (status, last `seq`, a Reconnect button);
   - `GET /api/config` rendered as a read-only list: tag, poll interval, max concurrency,
     repositories and enabled messaging providers. The server omits secrets.

## Tests
- vitest:
  - `JobTable` sorts waiting jobs first;
  - the flash class is applied when a value changes;
  - the filter syncs with the query string;
  - the modal rejects `abc` and `-1`.
- `Bff.Tests`: `/api/config` never contains the keys `Pat`, `BotToken`, `ApiKey` or `ClientSecret`
  (a reflection test over the options tree).
- Manual: a 375 px width has no horizontal scroll; both themes pass a contrast spot-check.

## Done when
- [ ] The dashboard updates live as jobs change state, turns and cost.
- [x] The waiting counter and connection indicator are accurate and announced to screen readers.
- [x] A work item can be started from the UI.
- [x] The layout works on phone widths, in both themes.

## As built
- **Routes:**
  - `/`: Dashboard;
  - `/jobs/:id`: a minimal job page until the Session view (T3.10);
  - `/history`: a placeholder until T3.11;
  - `/settings`;
  - a 404 page.

  The job, history and settings views are lazy-loaded.
- **Shell:**
  - The links are Dashboard / History / Settings.
  - The waiting badge links to `/?filter=waiting` and is labelled for screen readers.
  - `ConnectionIndicator` has `role="status"` and `aria-live="polite"`.
  - On phones the links fold into a daisyUI dropdown on a native `<details>` element, not a
    `drawer`: no script, and the same result.
  - The theme toggle is hidden below `sm` (it's on Settings).
- **`StatsBar` doesn't show cost:** agentd runs on a Claude subscription, so there's no per-job
  cost. "Done today" isn't shown either, because the dashboard snapshot only has active jobs.
  - The tiles are Running x/max (Preparing counts as running), Waiting for you, Queued and In review
    (+ Publishing).
  - `AgMeter` shows the agent slots.
  - Max concurrency comes from `/api/config`.
- **`JobTable`:**
  - Columns: state, WI + title, repo, branch, phase, elapsed. Elapsed ticks every 10 s for active
    jobs.
  - Waiting rows are tinted.
  - A changed row (state, phase, PR, fix rounds) flashes `bg-primary/10` for 600 ms.
  - `j` / `k` / Enter work.
  - Row actions: Work item ↗ (the Azure DevOps URL built from `/api/config` repositories), PR ↗,
    Retry (Failed) or Cancel. Cancel asks first in an `AgModal`.
  - There's no "Open chat ↗": conversation links are on the job detail, not the summary. They come
    with the Session view.
- **Filters:** an `AgToggleGroup` used as a single choice, synced to `?filter=`, plus a client-side
  search by title or `WI-id`.
- **`RunWorkItemModal`:** accepts only a positive whole number (`abc`, `-1`, `0`, `1.5` are refused
  before any request). It calls `POST /api/workitems/{id}/run`, then shows a toast and navigates to
  the new job.
- **`GET /api/config`:** `GetConfigSummary` → `ConfigVm`.
  - Fields: tag, claim tag, poll interval, max concurrency, plan approval / review loop / hand-off,
    repositories (name, organization, project, base branch) and the enabled chat providers.
  - The view model is explicit, and a test checks that no key or property names a secret.
- **Favicon:** `public/favicon.svg`, linked in the layout.
- **Tests:**
  - vitest: row flash; waiting first; the filter ↔ query string sync; the modal refuses bad IDs.
  - `Bff.Tests`: `/api/config` contents and the no-secrets check.
- **Smoke test** on the demo database in headless Chrome at 375 px: no horizontal scroll in either
  theme, Live, no console errors, and Settings renders.
- **Still open:** "updates live as jobs change state" needs a running job. The demo database has
  none active, so it gets checked with the next real run. The store tests cover the refresh path.
