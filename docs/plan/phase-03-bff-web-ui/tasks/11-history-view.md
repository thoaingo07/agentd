# T3.11 — History view with server-side paging

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | T3.2, T3.9, T3.10 | S | web / Agentd.Application |

## Goal
List finished, failed and cancelled jobs with **server-side paging and filters**, and open any of
them in the same Session view as a replay (the same components, with no live events).

## Files
- `src/Agentd.Application/Queries/SearchHistory.cs` — modify (from T3.2): filters, sorting, paging.
- `src/Agentd.Infrastructure.Persistence/Queries/HistoryQuery.cs` — create: an efficient SQL query.
- `src/Agentd.Web/ClientApps/dashboard/views/HistoryView.vue` — create.
- `src/Agentd.Web/ClientApps/dashboard/stores/history.ts` — create: a small store (page state, filters, results).

## Implementation
1. **Query:** `GET /api/history?state=&repo=&q=&from=&to=&page=1&pageSize=25`.
   - `state` ∈ `Done | Failed | Cancelled` (multiple allowed); `q` matches the title (ILIKE) or an
     exact `WI-id`; the date range applies to `completed_at`.
   - Sorted by `completed_at DESC`.
   - Returns `{ items: JobSummaryVm[], page, pageSize, total }`. `total` comes from a separate
     `COUNT(*)`, capped at 10,000 and displayed as "10,000+".
   - Indexes: `(state, completed_at desc)` and a trigram index on `title` if `pg_trgm` is available.
     Otherwise plain ILIKE is acceptable at this scale.
2. **Store:** `history.ts` holds `filters`, `page`, `items`, `total`, `loading` and `error`.
   - Filters sync with the URL query, so views can be shared and survive a reload.
   - The text search is debounced to 300 ms.
3. **View:**
   - reuses `JobTable` (T3.9) with an extra "Completed" column and no live flash;
   - filter controls: an `AgToggleGroup` for state, a repo `<select>`, a text input and a date range
     (native `<input type="date">`);
   - a daisyUI `join` pagination control; empty and error states use daisyUI `alert`.
4. **Replay:** clicking a row opens `/jobs/:id`. `SessionView` already works for final jobs:
   `events.open` loads the history, the hub subscription receives nothing new, and the composer is
   disabled with "Job finished". No extra code should be needed; confirm it here.

## Tests
- `Infrastructure.Tests`: the filters combine correctly, paging boundaries hold, `q` with `WI-1234`
  matches exactly, and `total` is capped.
- vitest: filters ↔ query string round-trip; the debounce issues one request.
- Manual: open a failed job from History → the full transcript and `ErrorAlert` are visible, and the
  composer is disabled.

## Done when
- [x] History pages on the server, with filters that survive a reload.
- [x] Any finished job can be replayed in the Session view.
- [x] The query is index-backed (checked with `EXPLAIN` on a seeded table of about 10k jobs).

## As built
- **"Completed" is a finished job's `updated_at`.** There's no `completed_at` column: a final job
  isn't changed again, so its last update is when it finished. That avoids a column plus a backfill.
  - `JobSummaryVm.completedAt` is set for Done / Failed / Cancelled jobs.
  - History sorts by `updated_at DESC, id DESC`.
- **Routines:** `agentd.job_search` / `job_search_count` gained `p_work_item`, `p_from`, `p_to`, and a
  count cap.
  - The old signatures are dropped in the routine file.
  - The count stops at cap + 1 (`LIMIT` in a subquery), so a huge history never costs a full count.
  - The API returns `total` (at most 10,000) and `totalCapped`; the UI shows "10,000+".
- **Text search:**
  - `WI-1234` (or `wi1234`) matches exactly that work item;
  - plain digits match the title or the work item id;
  - anything else is a case-insensitive title substring (`SearchHistoryHandler.ParseText`).
- **Dates:** `from` / `to` are calendar days (`yyyy-MM-dd`). `to` is inclusive in the UI and
  exclusive (next midnight, UTC) in SQL. `from` after `to` → 400.
- **Index:** migration `202610080001_history_index` adds `ix_jobs_history (updated_at DESC, id DESC)`.
  - `EXPLAIN ANALYZE` on 10,000 seeded jobs: the history page (with or without a state filter) is an
    index scan on `ix_jobs_history`, 0.05 ms for a page of 25.
  - A `(state, updated_at)` index was tried and never chosen, so it was dropped.
  - No trigram index: a title substring needs a filtered scan, which is fine at this scale.
- **`IJobSearch`** takes a `JobSearchFilter` record (states, repository, title, work item, from, to)
  instead of a long parameter list.
- **Store and view:**
  - `stores/history.ts` handles `toQuery` / `fromQuery`, and keeps only the newest response when
    requests overlap.
  - `HistoryView` makes the URL the source of truth (back/forward and reload land on the same page).
  - Text search waits 300 ms after the last keystroke; any filter change goes back to page 1.
  - Controls: an `AgToggleGroup` for Done / Failed / Cancelled, a repository `<select>` (from
    `/api/config`), native date inputs, the search box, and a daisyUI `join` pager.
- **`JobTable`** has `live: false` for History: no change flash, a Completed column, and "Took"
  instead of a ticking Elapsed.
- **Replay** needed no new code: `/jobs/:id` loads the stored transcript, and the hub sends nothing
  new. The composer now says "Job finished." for final jobs.
- **Tests:**
  - Infrastructure: filters combined (state + repo + title, case-insensitive); paging and past the
    end; exact work item; date window (from inclusive, to exclusive); count cap.
  - Application: the text rules.
  - Bff: `from` / `to` reach the query.
  - vitest: filters ↔ query string round-trip and junk handling; the URL drives the first load; a
    burst of typing makes one request and resets the page.
- **Smoke test** on the demo database in headless Chrome: History lists the 3 finished WI-5613 jobs
  newest first, with completion times. Searching `WI-5613` updates the URL. Clicking a row replays
  job #3 (40 tool cards, composer disabled with "Job finished."). No console errors.
