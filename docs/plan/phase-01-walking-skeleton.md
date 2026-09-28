# Phase 1 — Walking skeleton: Azure DevOps → Claude → PR

**Goal:** the thinnest end-to-end path. A work item tagged `ai-workflow` is claimed, Claude Code
works on it in its own git worktree, and a PR linked to the work item is opened. There is no chat,
no UI and no workflow phases yet; each job is a single Claude run.

Design refs: [Architecture §3.2–3.7, §3.10](../architect/README.md) ·
[Azure DevOps reference](../architect/references/azure-devops.md) · [Claude Code reference](../architect/references/claude-code.md)

---

## Scope

**In**

- **Domain:** `Job` aggregate with minimal states `Queued → Preparing → Running → Publishing →
  Done | Failed | Cancelled`, the value objects (`WorkItemId`, `BranchName`, `ClaudeSessionId`, …),
  and domain events.
- **Application:** `PollWorkItems`, `ClaimWorkItem`, `StartNextJob`, `RecordAgentOutput`,
  `FinishWork`, `PublishPullRequest`, `CancelJob`, `RecoverJobsOnStartup`, and the ports they need.
- **`Infrastructure.AzureDevOps`:**
  - `AzureCliCredential` and PAT token providers;
  - WIQL query, work item read, claim tag (JSON Patch with a `/rev` test), comment;
  - push and create PR with `workItemRefs`.
- **`Infrastructure.Git`:** worktree add, remove and prune; push.
- **`Infrastructure.Claude`:**
  - `ClaudeCodeRunner` (one profile from config, `ArgumentList`, secret-stripped environment);
  - a stream-json parser → the `events` table;
  - per-job transcript files.
- **`Agentd.Mcp`:** the `/mcp` endpoint with a per-job bearer token; tools `report_progress`
  (log only for now), `finish`, and `get_work_item`.
- **Host workers:** `WorkItemPollingWorker` and `SchedulerWorker` (`MaxConcurrent`, a
  `FOR UPDATE SKIP LOCKED` dequeue), plus startup recovery.
- **CLI verbs:** `agentd run <workItemId>` (skips polling) and `agentd status`.
- Repository registration from config (`Repositories[]`), with matching by area path or a
  `repo:<name>` tag.

**Out:** chat or `ask_developer` (Phase 2), UI (Phase 3), phases, gates and kit (Phase 4),
multiple model profiles (Phase 6).

---

## Tasks

1. Domain `Job` + state machine, with unit tests for every allowed and forbidden transition.
2. EF mappings for `jobs` and `events`, and a migration.
3. The ADO client (both auth modes) with recorded-HTTP tests; a manual smoke test against the real
   organization.
4. `WorktreeManager`: branch `ai/<id>-<slug>` from `origin/<base>`; cleanup; prune on startup.
5. `ClaudeCodeRunner`:
   - build the arguments (`-p`, `--session-id` / `--resume`, `--output-format stream-json --verbose`,
     `--mcp-config`, `--allowedTools`, `--permission-mode`, `--max-turns`);
   - start the process, read stdout line by line, and handle exit codes and idle timeouts.
6. The stream-json parser with fixture tests (assistant text, tool_use, tool_result, result/error).
7. The MCP server (C# MCP SDK) with per-job token authentication, and `finish` → `FinishWork`.
8. `PublishPullRequest`: check for commits ahead of base → push → create the PR → comment on the
   work item. Idempotent if the PR already exists.
9. Workers + recovery: a `Running` job whose process is gone → resume with "agentd restarted;
   continue".
10. CLI verbs.

## Exit criteria (the demo)

- A test work item in the ADO sandbox project, tagged `ai-workflow`:
  1. within one poll interval it gets `ai-in-progress`;
  2. a worktree exists;
  3. `claude` runs, commits, and calls `finish`;
  4. a PR is opened, linked to the work item, and the work item gets a comment with the PR link.
- Killing agentd during a run and restarting it resumes the same Claude session.
- Two tagged work items run concurrently in separate worktrees (`MaxConcurrent: 2`).
- The Claude process environment contains no `Agentd__*` secrets (checked by a test).

## Risks / open questions for review

- **Sandbox:** which ADO organization, project and repo do we use for demos and tests?
- **Permission mode for v1:** `acceptEdits` plus an allowlist (`Bash(git:*)`, `Bash(dotnet test:*)`, …)
  is proposed. Is that enough to be useful without being too permissive?
- **Work item → repo mapping:** area path or `repo:<name>` tag? (Architecture decision #4.)
- **PR creation:** use the REST API (as planned), or `az repos pr create`?
