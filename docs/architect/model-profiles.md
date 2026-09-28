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
| **Claude subscription** (Pro/Max/Team) | no base URL; login stored in the profile's own `CLAUDE_CONFIG_DIR` (`claude login` once per account, done by an operator) |
| **Anthropic API key** | `ANTHROPIC_API_KEY` |
| **DeepSeek** | DeepSeek's Anthropic-compatible endpoint + API key |
| **GLM (Zhipu / Z.ai)** | GLM's Anthropic-compatible endpoint + API key (or a GLM coding-plan key) |
| **Gemini** | **Non-coding steps** (retrospective, curator, bootstrap, optional reviewer) use MAF's native Gemini connector ([orchestration-maf.md §5](orchestration-maf.md#5-non-coding-agents-on-maf)). **Coding phases:** Gemini has no Anthropic-compatible endpoint, so traffic goes through a local **translating gateway** (for example a LiteLLM proxy on `127.0.0.1`) that exposes an Anthropic-style API in front of the Gemini API. Alternatively, add a second `IAgentRunner` implementation that drives the Gemini CLI (§7). |
| **Any other compatible endpoint or gateway** | `Kind: AnthropicCompatible` with a base URL |

Endpoint URLs and model IDs are **configuration**, not code. Providers rename models often. Check
each provider's current docs for Claude Code / Anthropic-compatible usage when adding a profile.

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
| `Kind` | `ClaudeSubscription`, `AnthropicApi` or `AnthropicCompatible` (later: `GeminiCli`, …) |
| `Model`, `SmallModel` | the model IDs at that provider |
| `MaxConcurrent` | concurrent processes on this profile, on top of the global `Agents.MaxConcurrent` |
| `Pricing` | per-million-token input/output (and cache) prices. **agentd computes cost itself** from token usage, because the CLI's `total_cost_usd` assumes Anthropic pricing. |
| `Capabilities` | e.g. `{ "Tools": "good", "LongContext": true, "Vision": false }`. The router refuses to put a phase on a profile that lacks a capability the phase requires. |
| `DailyBudgetUsd` | a hard stop per profile. Jobs fall back to the next profile, or wait. |

---

## 3. Routing: which profile runs which phase

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
- `jobs.claude_session_id` becomes a `job_sessions` table: `job_id`, `phase`, `profile`,
  `session_id`, `started_at`, `ended_at`, `end_reason`.

---

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
| Future `GeminiCliRunner : IAgentRunner` (or any other agent CLI) | a new `Infrastructure.<Runner>` project; the `IAgentRunner` port is unchanged |
| Per-profile semaphores, circuit breakers, budget counters | Infrastructure + Persistence |
| Profile, cost and health views | `Agentd.Bff` + `web/` |
