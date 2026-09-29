# Phase 6 — Model profiles & multi-provider routing

**Goal:** each phase of each job runs on the right model and account: Claude subscriptions or API
keys, DeepSeek, GLM, Gemini. The phase lists fall back automatically, a circuit breaker skips
failing profiles, budgets are enforced, and cost is tracked per phase.

Design refs: [model-profiles.md](../../architect/model-profiles.md) ·
[orchestration-maf.md §5–6](../../architect/orchestration-maf.md#5-non-coding-agents-on-maf)

---

## Scope

**In**

- **Domain:** `ModelProfile`, `PhaseRouting`, and the resolution rules (work item tag → kit →
  default, filtered by the operator's `AllowedProfiles`).
- **Application:** `IModelRouter`, `IProfileHealth`, the handoff prompt builder for profile switches,
  and the `job_sessions` table (per phase and profile).
- **`ClaudeCodeRunner` per-profile environment:**
  - `ANTHROPIC_BASE_URL`, the credential and `ANTHROPIC_MODEL`;
  - `ANTHROPIC_DEFAULT_HAIKU_MODEL`, `CLAUDE_CONFIG_DIR` and `API_TIMEOUT_MS`;
  - the other profiles' secrets stripped.
- **Per-profile concurrency semaphores, circuit breakers and daily budgets.** Cost is computed from
  token usage × `Pricing`, not from `total_cost_usd`.
- **Mid-phase fallback:** restart the phase on the next profile with the handoff (commits kept).
- **MAF `IChatClientFactory`** (Anthropic, OpenAI-compatible, Gemini) for non-coding agents, with
  cost and redaction middleware. `ReviewExecutor` can run as a MAF agent on a non-Claude profile.
- **`kit.json` `models` preferences**, capped by `Limits.AllowedProfiles`.
- **UI:** a **Models** page (health, concurrency, cost vs budget); per-phase profile and cost shown
  in the stepper.

**Out:** the MAF Harness Agent runner and its benchmark (Phase 10).

---

## Tasks

Task index: [tasks/README.md](tasks/README.md) (the detail files are written when this phase starts).

1. Profile config + validation (profiles referenced by routing must exist; secrets present).
2. The router with resolution and filtering, with unit tests for every precedence case.
3. Environment builder per profile kind; a test that no foreign secrets leak.
4. `job_sessions` + session pinning; handoff on a profile switch at a phase boundary.
5. Health: a breaker (N failures → cool-down), auth errors → the profile is disabled and a chat
   alert is sent; budgets.
6. Cost accounting from `turn.result` usage; per phase and per job; the daily per-profile rollup.
7. `IChatClientFactory` + MAF middleware; `ReviewExecutor` on a MAF agent (read-only tools).
8. Onboard profiles:
   - one Claude subscription (its own config directory);
   - one Anthropic API key;
   - DeepSeek and GLM (Anthropic-compatible endpoints);
   - Gemini for non-coding steps.
9. UI: ModelsView, stepper profile labels, `/api/models`.

## Exit criteria (the demo)

- A job with routing Design/Plan = Claude, Implement/Test = DeepSeek, Review = Gemini (MAF agent):
  - it completes;
  - the stepper shows the profile and cost for each phase;
  - `job_sessions` shows a new session at the Plan → Implement boundary, seeded with the handoff.
- Revoking the DeepSeek key mid-Implement → that profile's breaker opens → the phase restarts on
  GLM with its commits intact, and chat shows an alert.
- `kit.json` asking for a profile that isn't in `AllowedProfiles` is ignored, with a warning.
- Hitting a profile's daily budget stops new work on it; jobs fall back or wait.

## Risks / open questions for review

- **Exact endpoints and model IDs** for DeepSeek, GLM and Gemini at onboarding time; they are
  config, not code.
- **Tool-use quality of non-Claude models inside Claude Code**, especially for Implement. If it's
  poor, keep Claude for Implement and use cheap models only for Test fixes and non-coding steps.
- **Subscription accounts:** confirm the provider terms allow this automated use before onboarding
  subscription profiles.
- **Initial routing table:** what defaults do you want?
