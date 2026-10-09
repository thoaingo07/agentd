# Configuration

agentd reads, from lowest to highest priority:
1. its built-in defaults;
2. **`~/.agentd/config/agentd.json`**;
3. the **encrypted secrets** (`agentd secrets set`);
4. **`AGENTD_*` environment variables**;
5. command-line options.

In `agentd.json` the keys are relative to `Agentd`. As environment variables they're prefixed `AGENTD_`, with `__`
between levels: `AGENTD_Jobs__PermissionMode=Auto`.

`AGENTD_HOME` moves the whole home folder (default `~/.agentd`). Restart the daemon after changing configuration or
secrets (`agentd daemon restart`).

## A complete example

```json
{
  "AzureDevOps": { "Organization": "myorg", "Project": "MyProject", "Auth": "Pat" },
  "Repositories": { "Items": [ { "Url": "git@ssh.dev.azure.com:v3/myorg/MyProject/my-repo", "MatchTag": "repo:my-repo" } ] },
  "Scheduler": { "PollInterval": "00:01:00", "MaxConcurrent": 2 },
  "Jobs": { "RequirePlanApproval": true, "PermissionMode": "Ask" },
  "Messaging": { "Providers": { "Discord": { "Enabled": true, "GuildId": "<server id>", "ChannelId": "<channel id>" } } },
  "Users": [ { "Name": "alice", "Roles": ["Admin"], "Identities": { "Discord": "<alice's user id>" } } ],
  "Web": { "Urls": "http://127.0.0.1:7780" }
}
```

**Never put secrets in this file.** Store them with `agentd secrets set` (see [Secrets](#secrets)).

## Secrets

```bash
agentd secrets set AzureDevOps:Pat
agentd secrets list
```

- **How they're kept:** encrypted in `~/.agentd/config/secrets.json` with keys in `~/.agentd/keys` (both readable only
  by you). `list` shows names and dates, never values.
- **Agents never see them.** The one exception is a Claude token, which the Claude process itself needs.

| Secret | For |
|---|---|
| `AzureDevOps:Pat` | Azure DevOps with `"Auth": "Pat"` (scopes: Work Items read & write, Code read & write, Build read, Wiki read for `!chat`) |
| `AzureDevOps:ClientSecret` | Azure DevOps with `"Auth": "ServicePrincipal"` (with `TenantId` and `ClientId` in `agentd.json`; set from Settings → Azure DevOps) |
| `Claude:OAuthToken` | a token from `claude setup-token`, instead of logging in on the server |
| `Messaging:Providers:Discord:BotToken` | the Discord bot |
| `ConnectionStrings:agentd` | the PostgreSQL connection string (it holds the password) |

In containers, environment variables work too (`ConnectionStrings__agentd`, `AGENTD_AzureDevOps__Pat`, …).

## Settings you're most likely to change

| Setting | Default | What it does |
|---|---|---|
| `Scheduler:PollInterval` | `00:01:00` | how often Azure DevOps is polled for tagged work items |
| `Scheduler:MaxConcurrent` | `2` | jobs running at once |
| `Scheduler:Enabled` | `true` | `false` stops polling and starting jobs (the UI and chat keep working) |
| `Jobs:Tag` · `Jobs:ClaimTag` · `Jobs:AutoTag` | `ai-workflow` · `ai-in-progress` · `ai-auto` | the work item tags agentd uses |
| `Jobs:RequirePlanApproval` | `true` | wait for your "approve" before implementing (`ai-auto` skips it per work item) |
| `Jobs:ReviewLoop` · `Jobs:MaxFixRounds` | `true` · `5` | fix PR comments automatically, up to this many rounds |
| `Jobs:Handoff` | `true` | propose knowledge for the repository after the merge |
| `Jobs:PermissionMode` | `Ask` | `Ask` in chat for commands outside the allowlist, or `Auto`: allow them (see [Safety](safety.md)) |
| `Jobs:PermissionTimeout` | `00:10:00` | no answer in this time is a deny |
| `Jobs:WaitForHumanTimeout` | `3.00:00:00` | a question nobody answers fails the job after this |
| `Jobs:StuckAfter` | `00:05:00` | quiet this long → a "no activity" warning in the thread |
| `Jobs:RetainFailedWorktrees` · `Jobs:RetainFinishedWorktrees` | `3.00:00:00` · `1.00:00:00` | how long worktrees are kept after a failure / after the merge |
| `Claude:CommandTimeout` | `00:10:00` | the longest one shell command may run |
| `Claude:AllowedTools` | (git, read-only and build tools) | extra commands agents may run without asking, e.g. `"Bash(make:*)"` |
| `Claude:Model` | (the CLI's default) | the model for jobs |
| `Jobs:Steps:<step>:Model` · `…:Effort` | (`Claude:Model`) | a model and effort per step: `plan`, `implement`, `fix`, `handoff` (see below) |
| `Messaging:Providers:Discord:AllowEveryone` | `false` | accept messages from everyone who can post in the channel, not just `Users` |
| `Web:Urls` | `http://127.0.0.1:7780` | where the web UI and API listen (always a loopback address, e.g. `127.0.0.1`) |

## A model per step

Each step of a job can run on its own model. The session continues; only the model changes:
- `plan`: clarify and plan, read-only, before your approval;
- `implement`: after the plan is approved, until the pull request;
- `fix`: review fix rounds on the pull request;
- `handoff`: the knowledge proposal after the merge.
- `review`: not a job step; the default model and effort for `!review` (a review's own `--model` and `--effort` win).
- `chat`: not a job step; the default model and effort for `!chat`.

```json
"Jobs": {
  "Steps": {
    "plan":      { "Model": "opus",   "Effort": "high" },
    "implement": { "Model": "sonnet" },
    "fix":       { "Model": "sonnet", "Effort": "low" }
  }
}
```

A step that isn't listed uses `Claude:Model`, or the CLI's default. A change applies to the next turn of each job,
with no restart.

For example: plan on Opus, implement on DeepSeek flash (the next section), and review PRs with Opus 5.5 at high effort:

```json
"Jobs": {
  "Steps": {
    "plan":      { "Model": "claude-opus-5-5", "Effort": "high" },
    "implement": { "Profile": "deepseek" },
    "review":    { "Model": "claude-opus-5-5", "Effort": "high" }
  }
}
```

### Another provider for a step: DeepSeek

A step can also run on **another provider** that offers an Anthropic-compatible endpoint, such as DeepSeek. Claude Code
still runs the agent, so the tools, permissions and chat stay the same, but the model behind it is DeepSeek's.

**In the web UI:** **Settings → Models → Add DeepSeek** fills in the endpoint and models; paste the key, test, save,
and pick `deepseek` for a step under **Models per step**. Or by hand:

1. Store your DeepSeek API key, never in a file:
   ```bash
   agentd secrets set Models:Profiles:deepseek:ApiKey
   ```
2. Add the profile, and pick it for a step. Copy the endpoint and model IDs from
   [DeepSeek's Claude Code guide](https://api-docs.deepseek.com/quick_start/agent_integrations/claude_code) and
   [models & pricing](https://api-docs.deepseek.com/quick_start/pricing/) (`deepseek-flash`, `deepseek-v4-pro`); providers
   rename models.
   ```json
   "Models": { "Profiles": {
     "deepseek": {
       "BaseUrl": "https://api.deepseek.com/anthropic",
       "Model": "deepseek-flash",
       "SmallModel": "deepseek-flash",
       "Environment": { "CLAUDE_CODE_AUTO_COMPACT_WINDOW": "786432" }
     } } },
   "Jobs": { "Steps": { "fix": { "Profile": "deepseek" } } }
   ```
3. `agentd daemon restart`. It refuses to start if a profile has no key, a non-https URL, or a step names a profile
   that doesn't exist.

**How it behaves:**
- **Isolated:** a DeepSeek turn gets DeepSeek's endpoint, key and models, and nothing from your Claude subscription.
  It has its own Claude Code folder (`~/.agentd/claude/profiles/deepseek`).
- **Its own session:** a job's Claude conversation is never replayed to DeepSeek. The first DeepSeek turn of a job
  starts its own session with a handoff (the work item, the approved plan, "the branch has the work so far"), and
  later DeepSeek turns continue it. Coming back to Claude after DeepSeek turns, the Claude session gets a short "check
  `git log`" note.
- **No fallback or budgets yet:** those come with Phase 6. If DeepSeek is down, that step fails like any other turn,
  and `!retry` runs it again.

DeepSeek is weaker than Claude at tool use. A good start is `fix` or `implement` on DeepSeek and `plan` on Claude.

## Web access

agentd **only ever listens on loopback** (`127.0.0.1`). It refuses to start with any other address. By default
(`Auth:Mode` `None`) there is no sign-in: whoever reaches the port is the local admin. Requests that came through a
proxy are refused, so from another computer use an SSH tunnel ([Getting started, step 8](getting-started.md#8-reach-the-web-ui)).

**On your tailnet (Tailscale), the easy way:**
1. On the server: `tailscale serve --bg --https=443 http://127.0.0.1:7780`. The UI is then at
   `https://<machine>.<tailnet>.ts.net` for your tailnet only. Nothing is public, and never use `tailscale funnel`.
2. In `agentd.json`:
   ```json
   "Auth": { "Mode": "Tailscale", "Tailscale": { "AdminLogins": ["you@example.com"] } },
   "Web": { "Urls": "http://127.0.0.1:7780", "PublicOrigin": "https://<machine>.<tailnet>.ts.net" }
   ```
3. `agentd daemon restart`.

How sign-in works:
- **Identity:** each visitor is signed in by their **Tailscale login**, which Serve adds to every request and never
  lets a client fake.
- **Roles:** logins in `AdminLogins` are Admins and everyone else is a User. `AllowedLogins` narrows who gets in at all.
- **Other ways in:** an SSH tunnel still works and is the local Admin. Tagged devices and Funnel traffic aren't
  signed in.

To publish it on the internet safely, use **Cloudflare Tunnel + Cloudflare Access**:
1. **Tunnel:** `cloudflared tunnel create agentd`. Route `agentd.example.com` to `http://127.0.0.1:7780`, and **never
   publish `/mcp`** (the agents' endpoint).
2. **Access:** create a self-hosted application for `agentd.example.com`, with a policy for your email addresses.
3. **agentd:** configure it to check Access's token on every request:

```json
"Auth": {
  "Mode": "CloudflareAccess",
  "CloudflareAccess": {
    "TeamDomain": "<team>.cloudflareaccess.com",
    "Audience": "<the application's AUD tag>",
    "AllowedEmails": ["alice@example.com", "bob@example.com"],
    "AdminEmails": ["alice@example.com"]
  }
},
"Web": { "Urls": "http://127.0.0.1:7780", "PublicOrigin": "https://agentd.example.com" }
```

`AllowedEmails` (optional) narrows who Access lets in further; people in `AdminEmails` get the Admin role.
`PublicOrigin` is the address people type in the browser, so live updates accept it.
