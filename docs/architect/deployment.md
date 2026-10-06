# agentd — Deployment: a standalone VPS that works on any repo

**Goal:** install agentd on **any Linux VPS** (or workstation), configure it in the browser (a
first-run **setup wizard**), add **any repository** by URL, and let it work unattended. There's no source checkout, no Aspire, and nothing tied to one developer machine.

The shape is inspired by [NetClaw](https://github.com/netclaw-dev/netclaw): **one `agentd` binary**
with a daemon and a CLI, a **config home** (`~/.agentd`), **`init` / `doctor`**, **encrypted
secrets**, a **systemd** user service, a **Docker** image, and **releases with `update`**.

Related: [Architecture](README.md) · [Data access](data-access.md) · [Model profiles](model-profiles.md) ·
[Security](../security/README.md) · [SSO](../security/authentication.md) · [Plan: Phase 1b](../plan/phase-01b-portable-distribution/README.md)

---

## 1. One binary: daemon + CLI

`agentd` is a single self-contained executable. The daemon is the Host, and every other verb is a
CLI command that talks to the daemon over a **Unix domain socket** (`~/.agentd/run/agentd.sock`).
Where the daemon isn't needed, the CLI talks to PostgreSQL through the Application layer.

| Command | Does |
|---|---|
| `agentd daemon run` | runs the daemon in the foreground (what systemd and Docker execute) |
| `agentd daemon install \| uninstall \| start \| stop \| status \| logs` | manages the **systemd user service** (`agentd.service`) |
| `agentd init` | **headless** alternative to the web setup wizard (§5a): writes `~/.agentd/config/agentd.json`, generates the SSH key, stores secrets, checks the prerequisites |
| `agentd doctor [--repo <name>]` | checks everything (§6) and prints fixes |
| `agentd db migrate` | applies the schema (the Migrator runs in-process) |
| `agentd repo add <url> [--name] [--base] [--tag repo:<x>] [--area-path …]` / `repo list` / `repo remove` | registers **any** repository (§3) |
| `agentd run <workItemId> [--repo]` · `agentd status [--all]` | starts a work item now · lists jobs |
| `agentd secrets set \| list \| remove <key>` | the encrypted secret store (§4) |
| `agentd kit init <repo>` | opens the ai-sdlc kit PR ([ai-sdlc-kit.md](ai-sdlc-kit.md)) |
| `agentd update [--channel stable\|beta]` · `agentd version` | self-update from GitHub Releases |

Verbs parse with **System.CommandLine**, which gives subcommands, help and tab completion.

---

## 2. The config home: `~/.agentd`

```
~/.agentd/                      # AGENTD_HOME overrides the location
├── config/
│   ├── agentd.json             # operator config (the same schema as appsettings "Agentd")
│   └── secrets.json            # encrypted values (§4); never readable by agents
├── keys/                       # Data Protection keys (secrets + cookies); 0700
├── ssh/
│   ├── id_ed25519(.pub)        # agentd's own git identity (§5)
│   └── known_hosts
├── repos/                      # managed bare clones: <host>/<org>/<project>/<repo>.git
├── worktrees/                  # one per job: <repo>/wi-<id>
├── claude/                     # CLAUDE_CONFIG_DIR per model profile (sessions, subscription logins)
├── logs/                       # per-job transcripts (the daemon logs to journald/stdout)
└── run/agentd.sock             # CLI ↔ daemon
```

**Configuration layering** (lowest → highest priority):

1. built-in defaults;
2. `~/.agentd/config/agentd.json`;
3. environment variables with the **`AGENTD_`** prefix (e.g. `AGENTD_AzureDevOps__Organizations__0__Url`);
4. command-line options.

Secrets come from `secrets.json` (or from the environment in containers). `appsettings*.json` stays
only for development in the repository.

---

## 3. Any repository: managed clones

Repositories aren't local folders in config. agentd **clones and owns** them:

- `agentd repo add git@erm-azdo:v3/ermsystem/Portal/sysmin` (or `https://dev.azure.com/ermsystem/Portal/_git/sysmin`)
  - parses the provider, organization, project and repo from the URL (Azure DevOps now; GitHub
    later, behind the same `IPullRequestService` port);
  - detects the **default branch** from the remote (`git ls-remote --symref … HEAD`), e.g. `develop`;
  - stores the registration in the **`repositories`** table (the config file can seed entries too);
  - creates a **bare clone** in `~/.agentd/repos/…` (`git clone --bare`, plus a refspec so
    `origin/*` branches exist).
- **Each job:** `git fetch` in the bare clone → `git worktree add ~/.agentd/worktrees/<repo>/wi-<id> -b ai/<id>-<slug> origin/<base>`.
  Many jobs on the same repo share one clone, so the fetch is cheap.
- **Matching a work item to a repo** is unchanged: a `repo:<name>` tag wins, otherwise the longest
  area-path prefix.
- **Multiple organizations:** `AzureDevOps:Organizations[]`, each with its own auth (the `az` login,
  a PAT secret, or a service principal).


**From chat (added 2026-10-05):**
- **`!repo list`** shows the registered repositories and what matches them.
- **`!repo add <clone url> [--name x] [--tag t] [--base b] [--area-path p]`** and **`!repo remove
  <name>`** do the same as the CLI.
  - They need the **Admin** role from `Agentd:Users`. Strangers accepted by `AllowEveryone` are
    Operators, so they can't register repositories for agents to clone.
- **A work item tagged with an unknown `repo:<name>`** gets a comment naming that repository, the
  registered ones, and the `!repo add` to fix it. Previously it said only "no registered repository
  matches".
---

## 4. Secrets

- `agentd secrets set AzureDevOps:ermsystem:Pat`, `…Discord:BotToken`, `…Models:claude-max:OAuthToken` (from `claude setup-token`), …
- They are stored in `~/.agentd/config/secrets.json`, **encrypted with ASP.NET Core Data
  Protection** (keys in `~/.agentd/keys`, mode 0700). `list` shows the key names only.
- They are loaded as a configuration source by the daemon. **They are never placed in an agent's
  environment**, except the one credential its model profile needs
  ([model-profiles.md §6](model-profiles.md#6-security-and-terms)).
- In containers, the same keys can come from environment variables or mounted files instead.

**As built (T1b.2):**
- **Names are configuration paths** under `Agentd`: `AzureDevOps:Pat` fills `Agentd:AzureDevOps:Pat`. A name
  starting with `ConnectionStrings:` is used as is, so `ConnectionStrings:agentd` can hold the database password.
- **Layering:** `agentd.json` < `secrets.json` < `AGENTD_*` variables < command line. Containers can still override a
  secret with the environment.
- **`agentd secrets set <name>`** reads the value from **piped stdin or a no-echo prompt**, and refuses a value given
  as an argument, which would end up in the shell history and `ps`. `list` shows names, dates, who set them, and
  whether each one still decrypts. Values are at most 64 KiB.
- **Encryption:** Data Protection with purpose `agentd.secrets.v1`, on the daemon's key ring (`~/.agentd/keys`,
  application name `agentd`). The file is replaced atomically (temp file + rename, mode 0600) under a lock file.
- **A secret that no longer decrypts** (the key ring was replaced) is skipped, with one warning naming the key and
  never the value. Set it again to fix it.
- **The daemon reads secrets at startup:** restart it after a change.

---

## 5. Credentials on a headless server

| For | Recommended on a VPS | Alternatives |
|---|---|---|
| **Azure DevOps API** | a **PAT** per organization (a secret), scopes Work Items R/W, Code R/W, Build R | `az login --use-device-code` (works headless, but tokens expire and need a re-login); a service principal / workload identity |
| **git push / fetch** | **agentd's own SSH key**, generated by `agentd init` and printed so you can add it to Azure DevOps (or as a GitHub deploy key). agentd passes it with `GIT_SSH_COMMAND="ssh -i ~/.agentd/ssh/id_ed25519 -o UserKnownHostsFile=~/.agentd/ssh/known_hosts"`, so it doesn't depend on `~/.ssh/config` | HTTPS with the PAT |
| **Claude Code** | **the Claude subscription (the default; no API key needed):** a **long-lived token per subscription profile** (run `claude setup-token` once on any machine with a browser, paste the token into the wizard, and it's stored as a secret) | log the profile in on the server over SSH (`CLAUDE_CONFIG_DIR=~/.agentd/claude/<profile> claude auth login --claudeai`); an API key profile if you have one | an interactive `claude login` into the profile's `CLAUDE_CONFIG_DIR` over SSH |
| **Chat** | bot tokens as secrets | — |
| **Web UI** | served **by the daemon, on the same server** (decided 2026-10-03). **Not exposed publicly** without sign-in: an SSH tunnel, or **Cloudflare Tunnel + Cloudflare Access** with `Auth.Mode = CloudflareAccess` (§7.1); SSO when bound beyond loopback ([authentication.md](../security/authentication.md)) | Tailscale |

Headless Claude Code authentication must be verified against the installed CLI version during
Phase 1b (`agentd doctor` checks it).

---

## 5a. Setup UI: first-run wizard and Settings

Setting agentd up happens in the **browser**. `agentd init` is the headless equivalent, and both call
the **same Application use cases** (`SetupService`).

### Getting to the wizard safely (before any login exists)

1. On first start with no completed setup, the daemon binds **loopback only** and prints a
   **one-time setup link**: `http://127.0.0.1:7780/setup?token=…` (token: 32 random bytes; valid
   until setup completes; also in `agentd daemon logs` and via `agentd setup-link`).
2. You reach it over an **SSH tunnel** (`ssh -L 7780:127.0.0.1:7780 vps`) or Tailscale, and open the
   link. The token is exchanged for a short-lived **setup session** (an HttpOnly cookie), and the
   token is removed from the URL.
3. Once setup completes, the setup session ends, the token is invalidated, and from then on **only
   an Admin** (SSO, or the local admin created in the wizard) can change settings.

### Wizard steps (`ClientApps/setup`, a separate small SPA at `/setup`)

| Step | Asks for | "Test" button |
|---|---|---|
| 1. Admin access | SSO providers (Entra / Google client IDs + secrets) **or** a local admin account; which email becomes Admin | the sign-in round trip |
| 2. Database | PostgreSQL connection (or "use the bundled container" with Docker) → runs migrations | connect + `db migrate` |
| 3. Azure DevOps | one or more organizations: URL + **PAT** (or "use `az login` on the server") | a WIQL query per organization |
| 4. Git access | **Generate agentd's SSH key** → shows the public key with copy button + links to *Azure DevOps → SSH public keys* / GitHub deploy keys | `git ls-remote` to a URL you enter |
| 5. Models | **Claude subscription first:** paste the token from `claude setup-token` (with instructions), or "I logged in on the server"; optional extra subscriptions / DeepSeek / GLM / Gemini / API keys; the routing defaults | `claude auth status` + a tiny test prompt per profile |
| 6. Chat (optional) | Discord / Telegram bot tokens, channel/chat IDs, your user IDs | a test message |
| 7. Repositories | `repo add` by URL; the default branch is detected; the tag / area-path match | clone + fetch |
| 8. Review | a **doctor** report (every check ✅/⚠️/❌ with its fix), then **Finish** | — |

### After setup: Settings (Admin)

The same forms stay available under **Settings → Access, Azure DevOps, Git, Models, Chat,
Repositories**, and **Health** shows the live `doctor` report.

### How secrets are handled in the UI

- Secret fields are **write-only**. The UI shows "set · updated 2026-09-29 by tngo" and never the
  value. Submitting a new value replaces it, and there's a "Remove" action.
- Values travel only over the same-origin BFF (`POST /api/settings/*`, with antiforgery and the Admin
  policy). They are stored **encrypted** (`secrets.json`), never logged, never sent back to the
  browser, and **never put into agent environments**, except the one credential a model profile needs.
- Every change is audited (`settings.changed` event: who, what key, when; never the value).

---

## 6. `agentd doctor`

Each check prints ✅/⚠️/❌ and a one-line fix:

- the config and its schema are valid, and the secrets decrypt;
- PostgreSQL is reachable and the schema is up to date (otherwise: `agentd db migrate`);
- `git` ≥ 2.40; the SSH key exists and `git ls-remote` works against every registered repo;
- Azure DevOps: each organization authenticates, and the WIQL test query works;
- **Node ≥ 24 and the `claude` CLI** are present, and each model profile can authenticate (a tiny
  test prompt);
- the chat providers connect;
- disk space in `~/.agentd` and a free concurrency budget;
- `--repo <name>`: the repo's **toolchain** is present, meaning the commands in the kit's `verify`
  section resolve (`dotnet`, `npm`, `mvn`, …).

---

## 7. Install options

### A. Bare-metal VPS (recommended for "any repo")

Agents build and test the target repos, so the VPS needs **those repos' toolchains**. On a VPS
you control, you install them once (`apt install dotnet-sdk-10.0 nodejs …`), and `agentd doctor
--repo` tells you what's missing.

```bash
curl -fsSL https://github.com/thoaingo07/agentd/releases/latest/download/install.sh | bash   # → ~/.local/bin/agentd
docker run -d --name agentd-pg -v agentd-pg:/var/lib/postgresql/data -e POSTGRES_PASSWORD=… postgres:17   # or apt postgresql
agentd daemon install && agentd daemon start     # prints the one-time setup link
# on your laptop:  ssh -L 7780:127.0.0.1:7780 vps   → open the setup link → complete the wizard (§5a)
```

Headless alternative: `agentd init`, then `agentd db migrate`, then `agentd repo add <url>`.

Run it as a **dedicated, unprivileged `agentd` Unix user** with no sudo. Agents inherit that user's
permissions.

### 7.1 Publishing the Web UI with Cloudflare Tunnel + Access

The daemon keeps listening on `127.0.0.1` only, and `cloudflared` on the same server is the way in.

1. **Tunnel:** `cloudflared tunnel create agentd`. Add an ingress rule from `agentd.<your domain>`
   to `http://127.0.0.1:7780` that publishes `/`, `/api`, `/bff`, `/hubs` and `/_content`.
   **Never publish `/mcp`**, the agents' endpoint.
2. **Access:** create a self-hosted application for `agentd.<your domain>`, with an allow policy
   for your email(s) (email one-time code, GitHub or Google login).
3. **agentd:** set `Auth.Mode = CloudflareAccess`, plus `TeamDomain` and the application's
   `Audience` (its AUD tag). agentd verifies the `Cf-Access-Jwt-Assertion` token on every request,
   and uses its email as the user (T3.14):
   ```jsonc
   // ~/.agentd/config/agentd.json (none of these are secrets)
   "Agentd": {
     "Auth": {
       "Mode": "CloudflareAccess",
       "CloudflareAccess": {
         "TeamDomain": "<team>.cloudflareaccess.com",
         "Audience": "<AUD tag from the Access application>",
         "AllowedEmails": ["you@example.com"],   // optional, on top of the Access policy
         "AdminEmails": ["you@example.com"]
       }
     },
     "Web": { "Urls": "http://127.0.0.1:7780", "PublicOrigin": "https://agentd.<your domain>" }
   }
   ```
   The daemon still listens on `127.0.0.1` only. It trusts `X-Forwarded-Proto` from cloudflared,
   so it knows the browser is on https. `PublicOrigin` is only needed if the tunnel rewrites the
   `Host` header.

**Never point a tunnel or reverse proxy at the daemon in Mode `None`.** Every proxied request
arrives from `127.0.0.1`. That's why Mode `None` rejects requests carrying forwarding headers
(`X-Forwarded-For`, `Cf-Connecting-IP`, and so on), instead of treating them as the local user.

### B. Docker Compose

`ghcr.io/thoaingo07/agentd` (multi-arch) contains agentd + git + openssh + **Node 24 + Claude Code
CLI** (+ optional `az`). `compose.yaml` runs it together with PostgreSQL, with volumes for
`~/.agentd` and the Postgres data.

Toolchains for target repos are added by **extending the image**:

```dockerfile
FROM ghcr.io/thoaingo07/agentd:latest
RUN apt-get update && apt-get install -y dotnet-sdk-10.0 && rm -rf /var/lib/apt/lists/*
```

### C. Later: per-repo dev containers

Each job runs its agent inside the repo's **devcontainer** (`.devcontainer/devcontainer.json`), so
toolchains become per repo and agents are isolated from each other. This is planned for v2 (see
[Architecture decision #5](README.md#8-open-decisions)).

---

## 8. Operating a VPS

- **Sizing:** each running agent is one `claude` process plus the repo's builds and tests. Start
  with **4 vCPU / 16 GB** for `MaxConcurrent: 2–3`.
- **Network:** a firewall with only SSH open (or nothing public, with Tailscale). agentd binds the
  web UI to loopback or the Tailscale IP. Outbound traffic goes to Azure DevOps, Anthropic (or other
  model providers) and the chat platforms.
- **Updates:** `agentd update` downloads the new release, verifies its checksum, runs
  `db migrate`, and restarts the service. In-flight jobs resume from their checkpoints.
- **Backups:** `pg_dump` + `~/.agentd/{config,keys,ssh}`. The repos and worktrees can be recreated.
- **Worktree cleanup** (2026-10-06). Branches are never deleted.
  - **Done or cancelled jobs:** the worktree goes as before.
  - **Idea and review checkouts:** removed when their thread is closed.
  - **An hourly sweep** (`SweepWorktrees`) removes what nothing uses:
    - `wi-<id>`: 3 days after a failure (`Jobs:RetainFailedWorktrees`, so `!retry` can resume first), and 1 day after
      done (`Jobs:RetainFinishedWorktrees`, for talk-only follow-ups).
    - `idea-<id>`: once the idea is no longer being brainstormed or proposed.
    - `review-<id>`: once the review is closed or discarded.
    - Folders it doesn't recognise are left alone, then `git worktree prune` runs.
  - **`agentd doctor`** shows the number of checkouts, their size and the free disk; a warning below 5 GB or 5%.
- **Logs:** `agentd daemon logs` (journald), per-job transcripts in `~/.agentd/logs`, and an optional
  OpenTelemetry exporter.

---

### 8.1 Limitation: one machine per job (decided 2026-10-03)
A job runs and **resumes only on the machine that started it**. The database stores the Claude
session id, but the session content (`~/.claude/projects/<encoded-worktree-path>/<id>.jsonl`), the
worktree (`~/.agentd/worktrees/<repo>/wi-<id>`), unpushed commits and uncommitted edits are local.
Claude resolves a session by its working directory, so resuming needs the same machine and path.

- **Consequences:** run **one daemon per job set**. Don't point two daemons at the same database
  expecting failover. A machine that is lost mid-job loses that job's local state; the PR branch only
  has what was pushed, and the job can be retried.
- **If multi-machine resume is ever needed:** archive the session file per turn, push checkpoint
  commits per turn (`refs/agentd/checkpoints/wi-<id>`), use the same `AGENTD_HOME` path on every
  machine, and add worker leases. This is out of scope until then.

## 9. What changes in the code

| Area | Change |
|---|---|
| `Agentd.Host` | becomes the `agentd` executable: a System.CommandLine root; `daemon run` starts the web host and workers; the other verbs are CLI commands |
| Config | `~/.agentd` config home + `AGENTD_` env prefix + the encrypted `secrets.json` source |
| Repositories | a `repositories` table + `IRepositoryRegistry` (DB-backed, config-seeded); bare clones in `Infrastructure.Git` |
| Migrator | usable **in-process** by `agentd db migrate` (the Host references it). It still ships as its own console app for Aspire and CI. |
| Web assets | embedded in `Agentd.Web.dll` (`EmbeddedWebAssets`, a fallback after the files on disk), so the single-file `agentd` serves the UI by itself (T1b.6) |
| Release pipeline | a GitHub Actions workflow publishes self-contained builds (linux-x64/arm64, osx-arm64, win-x64) + checksums + `install.sh`, and a multi-arch Docker image to GHCR |
