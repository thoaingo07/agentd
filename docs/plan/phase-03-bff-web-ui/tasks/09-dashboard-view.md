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
- [ ] The waiting counter and connection indicator are accurate and announced to screen readers.
- [ ] A work item can be started from the UI.
- [ ] The layout works on phone widths, in both themes.
