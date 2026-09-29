# T4.11 — MCP `complete_phase`, gated `finish`, chat gate buttons & artifacts

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 4 | T4.2, T4.10, Phase 2 (messaging providers) | M | `Agentd.Mcp`, `Agentd.Application`, `Infrastructure.Messaging.*` |

## Goal
Give agents the tool to hand in a phase's artifact, only allow `finish` after Review, and let humans
approve or reject gates from chat (buttons in Discord and Telegram), with artifacts attached.

## Files
- `src/Agentd.Mcp/Tools/CompletePhaseTool.cs` — create: `complete_phase(phase, summary, artifact_markdown, applied_learnings[])`.
- `src/Agentd.Mcp/Tools/FinishTool.cs` — modify: rejects unless Review is complete.
- `src/Agentd.Application/Jobs/CompletePhase.cs` — create: the use case.
- `src/Agentd.Application/Messaging/GateMessages.cs` — create: builds the gate `OutboundMessage` (options + attachment).
- `src/Agentd.Application/Messaging/HandleInboundMessage.cs` — modify: map option IDs `gate:approve:<jobId>:<phase>` / `gate:reject:…` to `ApproveGate` / `RejectGate`.
- `src/Agentd.Infrastructure.Messaging.{Discord,Telegram}/…` — modify only if attachments or options need provider tweaks.

## Implementation
1. **`complete_phase`:**
   - validate that `phase` equals the job's current phase (otherwise return an error the agent can
     read: "You are in Implement; complete_phase(Plan) is not allowed");
   - validate that the artifact has the template's required headings (reusing T4.4's heading
     check). If headings are missing, return an error listing them, so the agent fixes the artifact
     instead of the job failing;
   - on success, call `CompletePhase`: store the artifact, write it to
     `<worktree>/.agentd-run/<phase>.md` (listed in `.git/info/exclude`), and tell the runner the
     phase is done. The tool answers "Phase recorded. End your turn now.";
   - `applied_learnings` is accepted and stored (used in Phase 9).
2. **`finish`:** the Domain `Finish` precondition (T4.2) is surfaced as a tool error: "finish is only
   allowed after Review passes".
3. **The gate message** (posted when a gate opens):
   - the text: "📋 Plan ready for WI-1234. Approve to start implementation.";
   - options: **Approve** (`gate:approve:<jobId>:plan`) and **Reject…** (`gate:reject:<jobId>:plan`);
   - the attachment: `plan.md` (the provider decides whether it becomes a file or an inline
     block);
   - if the provider doesn't support options, a numbered list plus the text commands
     `/approve` / `/reject <reason>`.
4. **Reject flow:** after clicking Reject, the bot asks "Reason?" in the thread. The next message
   from the same user becomes the reason (a 10-minute window). Otherwise it is recorded as
   "no reason given".
5. **Authorization:** in Phase 4, any mapped user can decide gates. Roles are enforced in Phase 5
   (Operator).
6. **Artifacts in chat:** every `complete_phase` posts a one-line summary + the artifact attachment
   to the job's conversations (coalesced with progress messages).

## Tests
- `complete_phase` in the wrong phase → a tool error; missing headings → a tool error listing them;
  valid → the artifact is stored and the `.agentd-run` file is written and git-excluded.
- `finish` before Review → a tool error; after → accepted.
- Inbound option `gate:approve:…` → `ApproveGate` is called once; a duplicate click is idempotent.
- Reject → the reason prompt → the next message becomes the reason → `RejectGate(reason)`.
- Contract tests: the gate message renders with buttons on Discord and Telegram fixtures, and as a
  text fallback without options.

## Done when
- [ ] Exit criterion: the plan gate shows buttons in chat, and approving continues to Implement.
- [ ] Artifacts appear in chat as attachments for every completed phase.
- [ ] `.agentd-run/` never appears in `git status` of the worktree.
