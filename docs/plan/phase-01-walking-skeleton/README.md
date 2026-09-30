# Phase 1 — Walking skeleton: Azure DevOps → Claude → PR

**Goal:** the thinnest end-to-end path. A work item tagged `ai-workflow` is claimed, Claude Code
works on it in its own git worktree, and a PR linked to the work item is opened. There is no chat,
no UI and no workflow phases yet; each job is a single Claude run.

Design refs: [Architecture §3.2–3.7, §3.10](../../architect/README.md) ·
[Azure DevOps reference](../../architect/references/azure-devops.md) · [Claude Code reference](../../architect/references/claude-code.md)

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

Detailed tasks: [tasks/README.md](tasks/README.md)

| ID | Task | Depends on | Size | Status |
|---|---|---|---|---|
| T1.1 | [Domain: `Job` aggregate and state machine](tasks/01-domain-job-state-machine.md) | Phase 0 | M | ☑ |
| T1.2 | [Application: ports and use cases](tasks/02-application-ports-and-use-cases.md) | T1.1 | M | ☑ |
| T1.3 | [Persistence: jobs, events, dequeue](tasks/03-persistence-jobs-and-events.md) | T1.1, T1.2 | M | ☑ |
| T1.4 | [Repository registration and matching](tasks/04-repository-registry.md) | T1.2 | S | ☑ |
| T1.5 | [Azure DevOps client](tasks/05-azure-devops-client.md) | T1.2 | L | ☑ |
| T1.6 | [Worktree manager and git push](tasks/06-worktree-manager.md) | T1.2, T1.4 | M | ☑ |
| T1.7 | [`ClaudeCodeRunner`](tasks/07-claude-code-runner.md) | T1.2, T1.6, T1.8 | L | ☑ |
| T1.8 | [stream-json parser](tasks/08-stream-json-parser.md) | T1.2 | S | ☑ |
| T1.9 | [MCP server with per-job token auth](tasks/09-mcp-server.md) | T1.2, T1.7 | M | ☑ |
| T1.10 | [`PublishPullRequest` (idempotent)](tasks/10-publish-pull-request.md) | T1.2, T1.5, T1.6, T1.9 | M | ☑ |
| T1.11 | [Host workers: polling, scheduler, recovery](tasks/11-workers-and-recovery.md) | T1.2–T1.10 | M | ☑ |
| T1.12 | [CLI verbs: `run` and `status`](tasks/12-cli-verbs.md) | T1.2, T1.11 | S | ☑ |

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
