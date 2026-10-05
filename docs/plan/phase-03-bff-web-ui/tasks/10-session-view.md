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
- `src/Agentd.Web/ClientApps/dashboard/views/SessionView.vue` — create: header, `AgTabs` (Transcript · Diff · Details), composer.
- `src/Agentd.Web/ClientApps/dashboard/components/EventList.vue` — create: windowing, follow mode, "N new events ↓", load earlier.
- `src/Agentd.Web/ClientApps/dashboard/components/EventItem.vue` — create: a switch on `event.type`.
- `src/Agentd.Web/ClientApps/dashboard/components/AssistantText.vue`, `ToolCallCard.vue`, `QuestionBubble.vue`, `ReplyBubble.vue`,
  `StateDivider.vue`, `TurnSummary.vue`, `ErrorAlert.vue` — create.
- `src/Agentd.Web/ClientApps/dashboard/components/DiffView.vue` — create.
- `src/Agentd.Web/ClientApps/dashboard/components/MessageComposer.vue` — create.
- `src/Agentd.Web/ClientApps/shared/utils/diff.ts` — create: the unified-diff parser.
- `src/Agentd.Web/ClientApps/shared/utils/markdown-lite.ts` — create: markdown → VNode tree (no HTML strings).
- `src/Agentd.Web/ClientApps/shared/utils/format.ts` — create: durations, cost, relative time.

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
- [x] Transcripts stream live, with correct pairing of tool calls and results.
- [x] Long sessions stay smooth: at most 2,000 rendered events, with load earlier working.
- [ ] The diff tab shows the branch diff and refreshes after edits.
- [x] There is no `v-html` anywhere; untrusted text always renders inert.
- [ ] A message from the UI resumes a waiting job and is mirrored to chat.

## As built
T3.10 is split in two PRs, so each stays under 1,000 lines.

**T3.10a: page, transcript, details, composer (this PR)**
- **Event types are the real ones**, not the placeholder names above. `transcript.ts` turns the
  window into rows:
  - `agent.text` → agent text, rendered as markdown;
  - `agent.tool_call` + `agent.tool_result`, paired by `id` / `toolUseId` → `ToolCallCard`;
  - `DeveloperQuestionAsked` → a question bubble (with numbered options);
  - `DeveloperReplied` → a reply bubble (who, and through which provider);
  - `JobFailed` → an error alert;
  - `phase.set` / `progress.reported` / `PullRequestCreated` and the other domain events → a state
    divider (`JobStarted` → "Job started");
  - `agent.result` → a turn summary;
  - `agent.session`, `agent.rate_limit` and `agent.other` are hidden.
- **Filters:** Text / Tools / Messages / State / Errors.
- **Files:**
  - `components/session/` (`EventList`, `EventRow`, `ToolCallCard`, `MessageComposer`,
    `transcript.ts`), instead of one component per event kind.
  - `shared/utils/markdown-lite.ts` plus `shared/components/MarkdownText.vue`, and
    `shared/utils/format.ts`.
- **markdown-lite:** paragraphs, `- ` lists, fenced code, `` `code` ``, `**bold**` and links. It
  builds VNodes, never HTML strings. Only http(s) URLs become links: `javascript:`, `data:` and
  relative links stay text, and `<script>` / `<img onerror>` render as text.
- **`ToolCallCard`:**
  - The header shows the tool name (`mcp__agentd__x` → `agentd:x`) and the most telling argument
    (command, file, pattern, …), plus a status: running dots, ✓, or ✗ failed.
  - Failed results start expanded.
  - Output over 200 lines is cut, with **Show all**. A payload trimmed by the live stream
    (`truncated`) is fetched in full from `/api/jobs/{id}/events/{seq}`.
- **`EventList`:**
  - Follow mode sticks to the bottom. Scrolling more than 80 px up stops it and shows
    "N new events ↓".
  - The button or `End` resumes it. With reduced motion, the scroll jumps instead of animating.
  - "Load earlier" keeps the reading position (it shifts `scrollTop` by the `scrollHeight` change).
- **Header:**
  - WI and title, the state badge, elapsed time (ticking every second), phase, repo, branch;
  - the session id (copy on click, taken from the `agent.session` event);
  - usage (5h / week).
  - No cost: agentd runs on a subscription.
  - Links: work item, one per chat thread, PR. Cancel or Retry with a confirmation.
- **Details tab:**
  - job id, attempt / resumes, plan status + estimate, fix rounds, hand-off, unread messages, last
    activity, last error.
  - The work item description isn't shown: no endpoint returns it yet. That waits for the Work item
    view (T3.13).
- **`MessageComposer`:**
  - enabled in `WaitingForHuman` and `Running`; Ctrl/⌘+Enter sends;
  - `POST /api/jobs/{id}/messages`;
  - shows an optimistic "sending" bubble until the `DeveloperReplied` event with the same text
    arrives.
- **Keyboard:** `1` / `2` switch tabs (outside inputs).
- **Tests (`tests/session.spec.ts`):**
  - markdown subset, plus hostile input staying inert;
  - durations and event names;
  - row building and call/result pairing;
  - a failed tool card starts expanded, and Show all works;
  - follow mode off on scroll-up, with the new-events count;
  - load earlier keeps the reading position;
  - the composer posts, and is disabled for finished jobs.
- **Smoke test** on demo job #3 in headless Chrome: 40 paired tool cards, 5 bubbles, 15 dividers,
  the work item / Discord thread / PR links, the composer disabled (the job is done), scrolled to
  the bottom, no console errors, no CSP violations.
- **Still open:** "a message from the UI resumes a waiting job" needs a live waiting job. It's
  checked by hand or in Playwright (T3.12). The endpoint and resume path have their own tests
  (T3.2b, Phase 2).

**T3.10b: Diff tab (next)**
- `diff.ts` parser, `DiffView`, inline diffs for Edit/Write tool calls, and a debounced refresh
  after edits.
