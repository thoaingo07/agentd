# T3.10 — Session view: transcript, diff, details, composer

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | T3.7, T3.8, T3.9 | L | web |

## Goal
Build `/jobs/:id`, the page for tracing one session live:
- a windowed transcript with collapsible tool calls, follow mode and "load earlier";
- a Diff tab using an in-house unified-diff parser;
- a Details tab;
- a message composer that resumes waiting jobs.

## Files
- `web/src/views/SessionView.vue` — create: header, `AgTabs` (Transcript · Diff · Details), composer.
- `web/src/components/EventList.vue` — create: windowing, follow mode, "N new events ↓", load earlier.
- `web/src/components/EventItem.vue` — create: a switch on `event.type`.
- `web/src/components/AssistantText.vue`, `ToolCallCard.vue`, `QuestionBubble.vue`, `ReplyBubble.vue`,
  `StateDivider.vue`, `TurnSummary.vue`, `ErrorAlert.vue` — create.
- `web/src/components/DiffView.vue` — create.
- `web/src/components/MessageComposer.vue` — create.
- `web/src/utils/diff.ts` — create: the unified-diff parser.
- `web/src/utils/markdown-lite.ts` — create: markdown → VNode tree (no HTML strings).
- `web/src/utils/format.ts` — create: durations, cost, relative time.

## Implementation
1. **Header:**
   - `WI-1234 · title`, `AgStateBadge`, elapsed (ticking every second), turns and cost;
   - repo · branch (mono) · session ID (short, with copy to clipboard);
   - links (work item, one per chat conversation, PR) with `rel="noopener noreferrer"`;
   - Cancel and Retry buttons, with an `AgModal` confirmation.
2. **Lifecycle:** `onMounted` → `events.open(id)` and `jobs` detail via `GET /api/jobs/{id}`;
   `onBeforeUnmount` → `events.close(id)`.
3. **Event rendering** ([design system §2.4](../../../design-system/README.md#24-event-type-colors-session-trace)):
   - `assistant.text` → `AssistantText`, rendered by the markdown-lite renderer. It supports
     paragraphs, `**bold**`, `` `code` ``, fenced blocks and links, and builds VNodes, so **no
     `v-html`** is needed;
   - `tool.call` + `tool.result` → one `ToolCallCard` (paired by tool-use ID) inside
     `AgCollapsible`:
     - the header shows the tool name (mono), an argument summary, status (✓ or ✗ exit code) and
       duration;
     - it is collapsed by default, except failed results and Edit/Write, which show an inline diff;
     - outputs over 200 lines are truncated, with **Show all**, which fetches the full event if
       `truncated`;
   - `ask_developer` → `QuestionBubble` (`chat-start`, warning tint); `message.inbound` or a web
     message → `ReplyBubble` (`chat-end`, primary tint, with a provider label);
   - `job.state_changed` → `StateDivider`; `turn.result` → `TurnSummary`; `job.error` → `ErrorAlert`.
4. **`EventList` windowing:**
   - render `events.windows.get(id).events` filtered by `ui.filters` (`AgToggleGroup`: Text, Tools,
     Questions, State, Errors);
   - a **Load earlier** button at the top when `hasMore`; keep the scroll position when prepending
     (measure `scrollHeight` before and after);
   - a list with `aria-live` only for question events.
5. **Follow mode:**
   - on by default; auto-scroll to the bottom on append;
   - the user scrolling up by more than 80 px turns it off and shows a floating
     **"N new events ↓"**;
   - clicking it or pressing `End` turns follow mode back on;
   - scrolling uses `scrollTop` assignment, not `scrollIntoView` with smooth behavior, when reduced
     motion is set.
6. **Diff tab:**
   - lazy-loaded on the first open → `GET /api/jobs/{id}/diff`;
   - reloads (debounced to 2 s) when a `tool.result` for Edit or Write arrives while the tab is
     visible;
   - `diff.ts` parses the unified diff into `{ files: [{ oldPath, newPath, status, binary, hunks: [{ header, lines: [{ kind, oldNo, newNo, text }] }] }] }`,
     handling renames, `\ No newline at end of file` and binary markers;
   - `DiffView` shows a file list on the left and a unified view on the right, with line numbers,
     and additions and deletions tinted `bg-success/10` and `bg-error/10`.
7. **Details tab:** work item fields (the description is sanitized server-side and shown as text),
   worktree path, attempts, last error, and links.
8. **`MessageComposer`:**
   - a textarea + Send button; `Ctrl+Enter` sends;
   - enabled in `WaitingForHuman` and `Running`, with a hint "queued as the next turn" when running;
   - `POST /api/jobs/{id}/messages`; the optimistic bubble is replaced by the real event when it
     arrives.
9. **Keyboard:** `1` / `2` / `3` switch tabs; `End` resumes follow mode.

## Tests
- vitest:
  - `diff.ts` handles additions, deletions, renames, binary files, multiple hunks and the
    no-newline marker;
  - `markdown-lite` escapes `<script>`, `<img onerror>` and `javascript:` links, which render as
    text or a safe `#` link;
  - `EventList` turns follow mode off on scroll-up and counts new events;
  - load earlier keeps the scroll anchor;
  - `ToolCallCard` pairs a call and result by ID, and a failed result starts expanded.
- Manual or Playwright (T3.12): a live job streams, and a message from the composer resumes a
  waiting job.

## Done when
- [ ] Transcripts stream live, with correct pairing of tool calls and results.
- [ ] Long sessions stay smooth: at most 2,000 rendered events, with load earlier working.
- [ ] The diff tab shows the branch diff and refreshes after edits.
- [ ] There is no `v-html` anywhere; untrusted text always renders inert.
- [ ] A message from the UI resumes a waiting job and is mirrored to chat.
