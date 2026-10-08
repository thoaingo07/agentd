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

agentd **never votes or approves**. After posting, it asks whether to delete or archive the chat thread.
