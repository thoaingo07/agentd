# agentd — Model Profiles (multi-provider, per phase)

agentd can run each **phase of each job** on a different model and account: Claude subscriptions or
API keys, DeepSeek, GLM, Gemini, and so on. A **model profile** is a named bundle of *endpoint +
credentials + model + limits + pricing*. **Routing rules** pick a profile, with fallbacks, for every
phase: Design, Plan, Implement, Test, Review, Retrospective and Distill.

Related: [Workflow & Learning](workflow-and-learning.md) · [Architecture §3.6](README.md#36-agent-runner-one-claude-process-per-job) ·
[Claude Code reference](references/claude-code.md)

---

## 1. How one runner talks to many providers

The Agent Runner still launches the **`claude` CLI** in the job's worktree. That keeps the same tools,
permissions, MCP servers, stream-json and session handling for every provider. What changes per
profile is the **process environment**:

| Env var (per process) | Purpose |
|---|---|
| `ANTHROPIC_BASE_URL` | the provider's **Anthropic-compatible** endpoint (unset → Anthropic) |
| `ANTHROPIC_AUTH_TOKEN` / `ANTHROPIC_API_KEY` | the credential for that endpoint (from a secret, never from config) |
| `ANTHROPIC_MODEL` (+ `--model`) | the main model ID at that provider |
| `ANTHROPIC_DEFAULT_HAIKU_MODEL` | the provider's cheap model, used by Claude Code for background tasks |
| `CLAUDE_CONFIG_DIR` | an isolated config and credential directory, **one per subscription account** |
| `API_TIMEOUT_MS` | longer timeouts for slower providers |

| Provider kind | How it connects |
|---|---|
| **Claude subscription** (Pro/Max/Team): **the default, and the first profile every install gets** | no base URL and no API key. Two ways to authenticate a profile: **(a)** a long-lived subscription token created once with `claude setup-token` (on any machine with a browser), stored as a secret and injected **only** into that profile's processes (as the environment variable the token command documents, e.g. `CLAUDE_CODE_OAUTH_TOKEN`; verify per CLI version); **(b)** an interactive login into the profile's own `CLAUDE_CONFIG_DIR` (`CLAUDE_CONFIG_DIR=~/.agentd/claude/<profile> claude auth login --claudeai`). Checked with `claude auth status --json` → `"authMethod": "claude.ai"`. |
| **Anthropic API key** | `ANTHROPIC_API_KEY` |
| **DeepSeek** | DeepSeek's Anthropic-compatible endpoint + API key |
| **GLM (Zhipu / Z.ai)** | GLM's Anthropic-compatible endpoint + API key (or a GLM coding-plan key) |
| **Gemini** | **Non-coding steps** (retrospective, curator, bootstrap, optional reviewer) use MAF's native Gemini connector ([orchestration-maf.md §5](orchestration-maf.md#5-non-coding-agents-on-maf)). **Coding phases:** Gemini has no Anthropic-compatible endpoint, so traffic goes through a local **translating gateway** (for example a LiteLLM proxy on `127.0.0.1`) that exposes an Anthropic-style API in front of the Gemini API. Alternatively, add a second `IAgentRunner` implementation that drives the Gemini CLI (§7). |
| **Any other compatible endpoint or gateway** | `Kind: AnthropicCompatible` with a base URL |

Endpoint URLs and model IDs are **configuration**, not code. Providers rename models often. Check
each provider's current docs for Claude Code / Anthropic-compatible usage when adding a profile.

---

## 1a. Runners: more than one agent CLI (added 2026-10-03)

Each step of the job cycle can run on a different model **and a different agent CLI**. So a profile
names its **runner** (the `IAgentRunner` that launches the process) as well as its provider:

| You want | `Runner` | `Kind` | How |
|---|---|---|---|
| **Claude Fable** in Claude Code | `ClaudeCode` | `ClaudeSubscription` (or `AnthropicApi`) | Only the model differs (`"Model": "claude-fable-5-1"`). Profiles for different models can share one subscription's `ConfigDir`. |
| **DeepSeek API** | `ClaudeCode` | `AnthropicCompatible` | DeepSeek's Anthropic-compatible endpoint (§1). No new runner. |
| **Codex CLI** (ChatGPT plan) | `CodexCli` | `ChatGptSubscription` | `codex login` into the profile's own `CODEX_HOME`, the counterpart of `CLAUDE_CONFIG_DIR`. |
| **Codex API** (OpenAI key) | `CodexCli` | `OpenAiApi` | The same runner, with `OPENAI_API_KEY` injected only into that profile's processes. |

**`CodexCliRunner`** (new project `Infrastructure.Codex`):
- **Launch:** `codex exec` in the worktree, with JSON event output, the profile's model, and its
  sandbox. Read-only steps (clarify, plan, hand-off proposal) use the read-only sandbox, like
  Claude's `ReadOnlyTools`. Exact flags are checked against the installed version
  (`codex exec --help`) when the profile is onboarded, and `agentd doctor` verifies them.
- **Sessions:** the session id from the first event is stored. Later turns of the same step resume
  it. A session is pinned to its runner and profile (§4).
- **agentd's MCP server** is configured per process in the profile's `CODEX_HOME`: the same
  `/mcp` URL and the same per-job bearer token. Every agentd tool (`set_phase`, `submit_plan`,
  `ask_developer`, `finish`, …) works unchanged.
- **Events:** the JSON events are mapped onto the existing runner-neutral types (`agent.text`,
  `agent.tool_call`, `agent.tool_result`, `agent.rate_limit`, `agent.result`; anything else is
  `agent.other`). The transcript, Web UI, heartbeat and usage warnings don't know which CLI ran.
- **Usage limits:** ChatGPT plans have rolling limits too. They feed the same 80% warnings and
  wait/fallback rules as Claude subscriptions (§4a).
- **Secrets:** a process only gets its own profile's credentials. Codex processes get no
  `ANTHROPIC_*`, and Claude processes get no `OPENAI_*`.

**Cycle steps → routing keys.** The Phase 2b steps reported through `set_phase` are the routing keys:

| Cycle step | Routing key |
|---|---|
| clarify | `Design` |
| plan | `Plan` |
| implement | `Implement` |
| verify | `Test` |
| PR review fixes | `Implement` (fix loops stay on the implementing profile) |
| independent PR review (Phase 7) | `Review` |
| hand-off (knowledge extraction) | `Distill` |

A switch of runner or profile can only happen **between turns**. When the next step routes to a
different profile, the current turn ends at that step boundary, and the next step starts a new
session with the handoff prompt (§4). `submit_plan` already ends a turn this way. `set_phase` ends
the turn only when the profile changes.

Example routing for this mix:

```jsonc
"Routing": { "Default": {
  "Design":    ["claude-fable"],
  "Plan":      ["claude-fable"],
  "Implement": ["codex-cli", "deepseek", "claude-fable"],
  "Test":      ["deepseek", "codex-cli"],
  "Review":    ["codex-api", "claude-fable"],     // a different family from the implementer
  "Distill":   ["deepseek"]
} }
```

---

## 2. Profiles

```jsonc
"Models": {
  "Profiles": {
    "claude-max-1":  { "Kind": "ClaudeSubscription",  "ConfigDir": "~/.agentd/claude/max-1", "Model": "claude-opus-5-5",   "MaxConcurrent": 2 },
    "claude-api":    { "Kind": "AnthropicApi",        "Model": "claude-sonnet-5",  "MaxConcurrent": 4,
                       "Pricing": { "InputPerMTok": 0.0, "OutputPerMTok": 0.0 } },           // fill in current prices
    "deepseek-flash":{ "Kind": "AnthropicCompatible", "BaseUrl": "<deepseek anthropic endpoint>", "Model": "<deepseek model id>",
                       "SmallModel": "<deepseek model id>", "MaxConcurrent": 4, "Pricing": { ... } },
    "glm-flash":     { "Kind": "AnthropicCompatible", "BaseUrl": "<glm anthropic endpoint>",      "Model": "<glm model id>",
                       "MaxConcurrent": 4, "Pricing": { ... } },
    "gemini-flash":  { "Kind": "AnthropicCompatible", "BaseUrl": "http://127.0.0.1:4000",        "Model": "<gemini model id>",
                       "Gateway": "litellm", "MaxConcurrent": 4, "Pricing": { ... } }
  },
  // secrets: Agentd__Models__Profiles__<name>__ApiKey (env / secret store)
  ...
}
```

Each profile has these properties:

| Property | Use |
|---|---|
| `Runner` | `ClaudeCode` (default) or `CodexCli` (§1a). Later: `GeminiCli`, … |
| `Kind` | `ClaudeSubscription`, `AnthropicApi` or `AnthropicCompatible` for `ClaudeCode`; `ChatGptSubscription` or `OpenAiApi` for `CodexCli` |
| `Model`, `SmallModel` | the model IDs at that provider |
| `MaxConcurrent` | concurrent processes on this profile, on top of the global `Agents.MaxConcurrent` |
| `Pricing` | per-million-token input/output (and cache) prices. **agentd computes cost itself** from token usage, because the CLI's `total_cost_usd` assumes Anthropic pricing. |
| `Capabilities` | e.g. `{ "Tools": "good", "LongContext": true, "Vision": false }`. The router refuses to put a phase on a profile that lacks a capability the phase requires. |
| `DailyBudgetUsd` | a hard stop per profile. Jobs fall back to the next profile, or wait. |

---

## 3. Routing: which profile runs which phase

> **Early version (2026-10-07, before Phase 6):** `Agentd:Jobs:Steps:<step>` gives each step of today's cycle its own
> **Claude model and effort**, with no profiles, accounts or fallback yet.
> - The steps are `plan` (clarify and plan, read-only), `implement`, `fix` (PR review rounds) and `handoff`.
> - The turn builders name each turn's step (`JobSteps.Of`), and the dispatcher fills in that step's model and effort
>   just before the runner starts it (`JobSteps.Apply`). The session stays the same: `--resume … --model`.
> - The setting is runner-neutral, so Phase 6 can replace it with `PhaseRouting` without touching the turn builders.
>
> **And other providers (2026-10-07, DeepSeek first):**
> - `Agentd:Models:Profiles:<name>` (only `Kind: AnthropicCompatible` for now) plus `Jobs:Steps:<step>:Profile`.
> - `ModelsOptionsValidator` fails startup on a missing key, a non-https URL, an unknown kind or an unknown profile.
> - `ProfileEnvironment` sets `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_MODEL`, the default Opus/Sonnet/Haiku
>   models and `CLAUDE_CODE_SUBAGENT_MODEL` (as DeepSeek's Claude Code guide lists them), plus the profile's own
>   `CLAUDE_CONFIG_DIR`. It removes the subscription token.
> - `ProfileSessions` implements §4: each profile has its own session per job, started with a handoff and resumed
>   after that. The job's own session is recorded as profile `default`; a session that missed turns gets a catch-up
>   note.
> - Still Phase 6: fallback, breakers, budgets, cost, and the Codex/Gemini runners.


```jsonc
"Models": {
  "Routing": {
    "Default": {
      "Design":        ["claude-max-1", "claude-api"],
      "Plan":          ["claude-max-1", "claude-api"],
      "Implement":     ["deepseek-flash", "glm-flash", "claude-api"],
      "Test":          ["deepseek-flash", "glm-flash"],        // fix loops continue in Implement's profile
      "Review":        ["gemini-flash", "glm-flash"],          // prefer a different model family from Implement
      "Retrospective": "same-as-implement",
      "Distill":       ["glm-flash", "claude-api"]
    }
  }
}
```

Per-repo preferences go in the repo's `.agentd/kit.json` (`"models": { "review": [...] }`). They are
**capped** by the operator's `Repositories[].Limits.AllowedProfiles`, so a repo cannot route itself
to a profile the operator hasn't allowed ([ai-sdlc-kit.md §5](ai-sdlc-kit.md#5-what-stays-with-the-operator-agentd-config-vs-the-team-kit)).

- **Resolution order:** work item tag (`ai-model:<phase>=<profile>`, e.g. `ai-model:implement=claude-api`)
  → repo kit (`.agentd/kit.json` `models`) → `Default`. Every result is filtered by the operator's
  `AllowedProfiles` for that repo.
- **Each list is a fallback chain.** The first *available* profile wins. A profile is available when:
  - it has concurrency headroom;
  - it is under its daily budget;
  - its circuit breaker is closed;
  - it meets the phase's required capabilities.
- **Suggested defaults:**
  - a strong model for **Design and Plan**, where mistakes are most expensive;
  - fast, cheap models for **Implement and Test**, which use the most tokens;
  - a **different model family** for **Review**, because an independent model catches different
    mistakes than the author;
  - a cheap model for **Distill**.

---

## 4. Sessions across profiles

A Claude Code session is stored locally (under `CLAUDE_CONFIG_DIR`), and its history contains
provider-specific content, so **a session is pinned to one profile**.

- **Phases on the same profile** continue the same session (`--resume`), as before.
- **Switching profile at a phase boundary** starts a **new session** that is seeded with a
  **handoff prompt**. The phase artifacts become the contract between sessions: the work item,
  `design.md`, `plan.md`, the test report, the branch and diff summary, and the developer Q&A so far.
  This is one more reason every phase must produce a complete artifact.
- **A fallback in the middle of a phase** (e.g. a provider returns 429 or 5xx repeatedly) restarts
  that phase on the next profile, with the same handoff plus "the previous attempt got this far:
  <commits so far>". Commits already on the branch are kept.
- `jobs.claude_session_id` becomes a `job_sessions` table: `job_id`, `phase`, `profile`, `runner`,
  `session_id`, `started_at`, `ended_at`, `end_reason`.

**Built first (2026-10-07, for DeepSeek before Phase 6):**
- `agentd.job_sessions (job_id, profile, session_id, created_at)`, one row per job and profile.
  `job_session_get_or_create` is safe under concurrent callers: the first insert wins.
- The job's original session (`jobs.claude_session_id`) stays the default profile's.
- `agentd.job_plans` keeps the plan the agent submitted, so a session that starts mid-job gets it in its handoff.
- The phase, runner and end-reason columns come with Phase 6.

---

## 4a. Subscription usage limits

A subscription has **usage limits** (session and weekly windows) instead of per-token billing.
agentd treats hitting a limit as **"paused", not "failed"**:

- **Detection:** the runner recognizes the CLI's usage-limit / rate-limit outcome (the stream-json
  `result` error and its message, which includes the reset time where the CLI reports one) and
  returns `UsageLimited(resetAt?)`.
- **Behavior:**
  - the job keeps its state and session;
  - it is re-queued with `not_before = resetAt` (or an exponential backoff);
  - chat and the UI show "⏸ waiting for the Claude usage limit to reset (~14:05)";
  - the profile's breaker opens until then, so no other job burns a turn on it.
- **Fallback:** if the phase's routing list has another available profile (e.g. a second
  subscription, or GLM), the phase continues there with the usual handoff (§4). Otherwise it waits.
- **Concurrency:** subscription profiles default to `MaxConcurrent: 1–2`, because parallel agents
  drain one subscription's window quickly.

## 5. Reliability and cost

- **Circuit breaker per profile.** After N consecutive failures (429, 5xx, auth errors), a profile is
  skipped for a cool-down period and `/healthz` reports it as degraded.
- **Auth errors** (for example an expired subscription login) disable the profile and alert in chat.
  Nothing is retried in a loop.
- **Cost:** the `turn.result` token usage × the profile's `Pricing` → the per-phase and per-job cost.
  The Web UI shows cost **per phase and per profile**, and a budget view per profile per day.
- **Quality feedback:** job outcomes (Review verdicts, fix-loop counts, human PR comments) are
  recorded per profile and phase, which gives the data to tune the routing table over time. The
  distiller can propose `workflow` learnings such as "profile X needs more than 2 fix loops on this
  repo; route Implement to Y."

---

## 6. Security and terms

- Credentials are per profile, from env or a secret store (`Agentd__Models__Profiles__<name>__ApiKey`).
  They are injected **only into that profile's process**, and the other profiles' and the daemon's
  secrets are stripped.
- Subscription profiles use a dedicated `CLAUDE_CONFIG_DIR` each, so accounts never share
  credentials or session files.
- **Code leaves the machine** to whichever provider runs a phase. Repositories can restrict the
  allowed profiles through the operator-owned `Repositories[].Limits.AllowedProfiles`
  (e.g. `["claude-api"]` for sensitive repos). A repo's kit cannot widen it.
- Respect each provider's terms for automated use and data handling. Multiple profiles are for
  **routing, failover and cost control**, not for evading a provider's usage limits.

---

## 7. Where it lives in the solution

| Concern | Layer |
|---|---|
| `ModelProfile` and `PhaseRouting` value objects; routing resolution rules | Domain |
| `IModelRouter` (phase + repo + WI → ordered profiles), `IProfileHealth` (breaker, budget) ports; the handoff prompt builder | Application |
| `ClaudeCodeRunner : IAgentRunner`: builds the env per profile (base URL, key, model, config dir) and launches `claude` | `Infrastructure.Claude` |
| `CodexCliRunner : IAgentRunner`: env per profile (`CODEX_HOME`, `OPENAI_API_KEY`), MCP config, JSON events → `agent.*` (§1a) | `Infrastructure.Codex` |
| Future `GeminiCliRunner : IAgentRunner` (or any other agent CLI) | a new `Infrastructure.<Runner>` project; the `IAgentRunner` port is unchanged |
| Per-profile semaphores, circuit breakers, budget counters | Infrastructure + Persistence |
| Profile, cost and health views | `Agentd.Bff` + `src/Agentd.Web/` |
