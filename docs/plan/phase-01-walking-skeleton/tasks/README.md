# Phase 1 — Tasks

The detailed implementation tasks for the [walking skeleton](../README.md), in build order.

| ID | Task | Depends on | Size | Status |
|---|---|---|---|---|
| T1.1 | [Domain: `Job` aggregate and state machine](01-domain-job-state-machine.md) | Phase 0 | M | ☑ |
| T1.2 | [Application: ports and use cases](02-application-ports-and-use-cases.md) | T1.1 | M | ☑ |
| T1.3 | [Persistence: jobs, events, dequeue](03-persistence-jobs-and-events.md) | T1.1, T1.2 | M | ☑ |
| T1.4 | [Repository registration and matching](04-repository-registry.md) | T1.2 | S | ☑ |
| T1.5 | [Azure DevOps client](05-azure-devops-client.md) | T1.2 | L | ☑ |
| T1.6 | [Worktree manager and git push](06-worktree-manager.md) | T1.2, T1.4 | M | ☑ |
| T1.7 | [`ClaudeCodeRunner`](07-claude-code-runner.md) | T1.2, T1.6, T1.8 | L | ☑ |
| T1.8 | [stream-json parser](08-stream-json-parser.md) | T1.2 | S | ☑ |
| T1.9 | [MCP server with per-job token auth](09-mcp-server.md) | T1.2, T1.7 | M | ☑ |
| T1.10 | [`PublishPullRequest` (idempotent)](10-publish-pull-request.md) | T1.2, T1.5, T1.6, T1.9 | M | ☑ |
| T1.11 | [Host workers: polling, scheduler, recovery](11-workers-and-recovery.md) | T1.2–T1.10 | M | ☑ |
| T1.12 | [CLI verbs: `run` and `status`](12-cli-verbs.md) | T1.2, T1.11 | S | ☐ |
