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
- [ ] History pages on the server, with filters that survive a reload.
- [ ] Any finished job can be replayed in the Session view.
- [ ] The query is index-backed (checked with `EXPLAIN` on a seeded table of about 10k jobs).
