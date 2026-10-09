# Reviewing pull requests

`!review` asks an agent to review **any open pull request** of a registered repository. The findings stay in the
thread until **you** choose what gets posted to the PR.

## Start

```text
!review 3944
!review 3944 check the Helm probes and whether readiness can fail --model opus
!review https://dev.azure.com/myorg/MyProject/_git/my-repo/pullrequest/3944 --focus security
```

- **The PR:** the first word. It can be a link, `!3944` or `3944`. A link names its repository; with a number, add
  `--repo <name>` unless only one repository is registered.
- **Your instructions:** everything after the PR, passed to the reviewer.
- **Options:** `--focus security,tests` names what to look at first; `--model` and `--effort` pick the model (`!model`
  / `!effort` in the thread change it later).
- **Refused:** completed or abandoned PRs.

agentd opens **🔍 Review: PR !3944: <title>** and checks out the PR's head **read-only**.

## A branch, without a PR

```text
!review branch:feature/keyset
!review branch:feature/keyset --base release/1.2 --repo sysmin
```

agentd fetches the pushed branch, compares it with where it left its base (the repository's base branch, or
`--base`), and answers with the review's page in the web UI. When the reviewer is done it says so in the same
channel: "📝 The review of `feature/keyset` → `develop` is ready: 🔴 1 🟠 0 … <link>". The findings, keep/edit/drop,
comments and Ask are on that page (see [Web UI → Reviews](web-ui.md#reviews)).

## Your own changes, on your laptop

```bash
cd ~/src/sysmin
agentd review                 # your uncommitted changes (staged and unstaged)
agentd review --base develop  # everything since develop: your commits plus uncommitted changes
agentd review --untracked     # include new files git doesn't track yet
agentd review --model opus --effort high
```

It runs **your own `claude`** (your login, your subscription) in your repository, **read-only**, with the same rules
as the server's reviewer. It doesn't need the agentd server. The findings are printed and saved to
**`.agentd/review.md`**, so you can tell your agent "fix what's in `.agentd/review.md`" (keep that file out of git).
The review page on your laptop, `--fix` and `--share` come next.

## What the reviewer looks at

Only two kinds of problems, so the review stays short:

- 🔴 **can break the app:**
  - a bug or wrong result, a crash or unhandled error, data loss;
  - a security hole (injection, missing authorization, secrets in the diff);
  - a breaking change (an API, contract, configuration key, or a migration without a safe rollback);
  - an acceptance criterion of the linked work item that isn't met.
- 🟠 **performance:** N+1 queries, unbounded loops or memory, blocking calls on hot paths, missing pagination or
  indexes for growing data.

Style, naming, docs, missing tests and "could be cleaner" are left out. The reviewer reads the code around each
change and **only reports what it could confirm**. It skips anything already raised in the PR's open comment threads.
At most 15 findings, worst first.

## Findings

```text
🔍 Review: Adds the deploy pipelines. One real bug; otherwise ready.
1. 🔴 Readiness probe path is wrong · charts/api/values.yaml:12
   It points at /health, which never fails, so broken pods get traffic.
   Fix: use /health/ready.
2. 🟠 Every request re-reads the whole config file · src/Config.cs:30
   Fix: read it once at startup and cache it.
1 post all to the PR · 2 keep in chat · 3 discard · or post 1,3 / drop 2. Ask me about any finding first if you like.
```

## Talk, then choose

**Keep talking:**
- "Why is #1 a problem?"
- "#2 is intended, drop it."
- "Look at the error handling in the API too."

The agent answers from the PR's code and sends a revised list when its findings change. If someone pushes new
commits, the next reply reviews the new head.

| Answer | What happens |
|---|---|
| **1** / `post` | each finding gets a short PR thread at its file and line, and **one main message** lists them all with their status |
| `post 1,3` | only those |
| `drop 2` | removes #2 and renumbers |
| **2** / `keep` | nothing is posted; the review stays in chat |
| **3** / `discard` | nothing is posted |

## On the pull request

```text
🤖 agentd review · 2 finding(s): 2 open, 0 fixed · checked at a1b2c3d

Adds the deploy pipelines. One real bug; otherwise ready.

|    | Finding                            | Where                        |
| 🔴 | Readiness probe path is wrong       | charts/api/values.yaml:12    |
| 🟠 | Every request re-reads the config   | src/Config.cs:30             |

🔴 can break the app · 🟠 performance · ✅ fixed · ⚪ closed
```

Each finding's own thread is just its title, why, and the fix. The main message is the one to follow: its icons turn
✅ when a finding is fixed.

## Keep reviewing until it's fixed

After posting, the chat thread stays open and agentd **re-checks** the findings:
- **automatically,** within a couple of minutes of a **push** to the PR or a **reply** on a finding's thread, while
  findings are open (one re-check per push);
- when you say **review again** (or `recheck`) in the review's thread;
- when you run **`!review <the same PR>`**: it continues that review in its thread instead of starting a new one.

When the PR is completed or abandoned, the review ends ("🏁 PR !3944 is completed: this review is done.").

A re-check reads the new commits and people's replies on each finding's thread, then updates the PR:

| The reviewer finds | On the PR |
|---|---|
| the problem is gone (checked in the code, not just the reply) | `✅ Fixed in b7e9f01.` on its thread, the thread is **resolved**, and the main message shows ✅ |
| the author's reply convinced it | its reply, e.g. "Agreed: /health is the readiness path here.", and the thread is **closed** (⚪) |
| still there | `Still open in b7e9f01: …` with what's still wrong (only when there's something new to say) |
| a new problem from the new commits | a new short thread, added to the main message |

The main message is edited in place: `x open, y fixed · checked at <commit>`. The chat thread gets one line:

```text
🔄 PR !3944 re-checked at b7e9f01: 1 fixed ✅, 1 open, 1 new.
```

agentd **never votes or approves**.
