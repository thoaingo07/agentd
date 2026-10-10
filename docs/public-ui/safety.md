# Safety: what agents can and can't do

Agents run on your machine, as your user. agentd keeps them in check in several layers.

## While planning: read-only

Until you approve the plan, the agent can **only read**: Read, Grep, Glob, `ls`/`cat`/`find`, and `git status/diff/log/show/fetch`.
It can't edit, build or commit. `!idea` and `!review` threads are always read-only.

## While implementing: an allowlist

These run without asking:
- editing files in the job's worktree;
- `git add/commit/diff/log/status/show/fetch`;
- `dotnet build/test/restore/format`, `npm ci/test/run`, `helm lint/template`;
- the usual text tools (`grep`, `sed`, `jq`, …).

Add your own with `Claude:AllowedTools` ([Configuration](configuration.md)).

Anything else becomes a **permission request** in the job's thread and on the web UI's work item page:

> Reply **1** allow once · **2** allow for this job · **3** always allow in `<repo>` · **4** deny

- No answer within `Jobs:PermissionTimeout` (10 minutes) counts as a deny.
- "Allow for this job" and "always" are remembered per command, e.g. `npm install` → `Bash(npm install:*)`. A broader
  rule covers narrower ones: `Bash(npm:*)` covers `npm install`.
- Remembered rules are listed, and can be revoked, in **Settings → Permissions** on the web UI.

### Auto mode

With `Jobs:PermissionMode` set to `Auto`, agentd allows those requests without asking and records them as decided by
"auto". The hard denies below still apply. Use Auto only for repositories and machines you trust the agent with.

## Never allowed

Whoever answers and whatever the mode, agentd always refuses:

| What | Why |
|---|---|
| `git push` | pushing is done by agentd, to the job's own branch only |
| `sudo` | no root access |
| `curl … \| sh` (and `wget`, `bash`, `python`) | running a downloaded script |
| `rm -rf /`, `~`, `$HOME` | deleting outside the worktree |
| anything touching `~/.agentd` or `AGENTD_HOME` | agentd's own files and secrets |

## Secrets stay with agentd

Agents start with a **clean environment**: agentd's secrets (PAT, bot token, database password) and its own settings
are removed. The only exception is the Claude token, which Claude itself needs. Agents also can't read `~/.agentd`.

## Other limits

- **One command at a time:** a single shell command may run for at most `Claude:CommandTimeout` (10 minutes), then
  it's stopped. The agent is told and can try another way.
- **Branch:** each job works on its own branch (`ai/<work item>-…`) in its own worktree. agentd pushes that branch
  and opens the PR. It **never approves, votes on, or merges** PRs: people do.
- **After the merge:** the thread stays open for questions, but only for talk. No more code changes or PRs
  ([Jobs](jobs.md)).
- **Who can talk to it:** only people listed in `Users` (unless `AllowEveryone` is on). Messages from anyone else are
  ignored silently.
- **Web UI:** it listens on loopback only, uses strict browser security (CSP, HttpOnly cookies, antiforgery), and
  needs Cloudflare Access to be reachable from the internet ([Configuration](configuration.md#web-access)).
