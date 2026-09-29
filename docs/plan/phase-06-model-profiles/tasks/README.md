# Phase 6 — Model profiles & multi-provider routing — Tasks

Task index for [Phase 6 — Model profiles & multi-provider routing](../README.md).

> **Detail files are written just in time, when this phase starts** (after the previous phases are built). They follow the same template as Phases 0–4, and are based on what earlier phases actually delivered, so they don't go stale.

| ID | Task | Status |
|---|---|---|
| T6.1 | Profile config + validation (profiles referenced by routing must exist; secrets present). | ☐ detail pending |
| T6.2 | The router with resolution and filtering, with unit tests for every precedence case. | ☐ detail pending |
| T6.3 | Environment builder per profile kind; a test that no foreign secrets leak. | ☐ detail pending |
| T6.4 | `job_sessions` + session pinning; handoff on a profile switch at a phase boundary. | ☐ detail pending |
| T6.5 | Health: a breaker (N failures → cool-down), auth errors → the profile is disabled and a chat alert is sent; budgets. | ☐ detail pending |
| T6.6 | Cost accounting from `turn.result` usage; per phase and per job; the daily per-profile rollup. | ☐ detail pending |
| T6.7 | `IChatClientFactory` + MAF middleware; `ReviewExecutor` on a MAF agent (read-only tools). | ☐ detail pending |
| T6.8 | Onboard profiles: - one Claude subscription (its own config directory); - one Anthropic API key; - DeepSeek and GLM (Anthropic-compatible endpoints); - Gemini for non-coding steps. | ☐ detail pending |
| T6.9 | UI: ModelsView, stepper profile labels, `/api/models`. | ☐ detail pending |
