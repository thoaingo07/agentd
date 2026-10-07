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

1. **Intent:** does the change do what its **linked work items' acceptance criteria** ask? Is anything out of scope?
2. **The diff in context,** and the repository's own rules (`AGENTS.md`, `CLAUDE.md`, `CONTRIBUTING.md`).
3. **In order:**
   - correctness;
   - security, including secrets in the diff;
   - breaking changes and database migrations;
   - error handling;
   - tests;
   - performance;
   - consistency.
4. **It checks before it claims.** It searches before saying something is missing or unused.
5. **It skips** formatter-level style, generated files, lockfiles, and anything already raised in the PR's open
   comment threads.

## Findings

```text
🔍 Review summary: Adds the deploy pipelines. One real bug; otherwise ready.
1. 🟠 major Readiness probe path is wrong · charts/api/values.yaml:12
   It points at /health, which never fails.
   💡 Use /health/ready.
2. 🟡 minor Image tag isn't pinned · azure-pipelines.yml:40
1 post all to the PR · 2 keep in chat · 3 discard · or post 1,3 / drop 2. Ask me about any finding first if you like.
```

**Severities:** 🔴 blocker (fix before merging) · 🟠 major (fix in this PR) · 🟡 minor (worth fixing) · ⚪ nit
(optional).

## Talk, then choose

**Keep talking:**
- "Why is #1 a problem?"
- "#2 is intended, drop it."
- "Look at the error handling in the API too."

The agent answers from the PR's code and sends a revised list when its findings change. If someone pushes new
commits, the next reply reviews the new head.

| Answer | What happens |
|---|---|
| **1** / `post` | every finding is posted as its own PR comment thread, at its file and line, plus a summary thread |
| `post 1,3` | only those |
| `drop 2` | removes #2 and renumbers |
| **2** / `keep` | nothing is posted; the review stays in chat |
| **3** / `discard` | nothing is posted |

agentd **never votes or approves**. Its comments start with "🤖 agentd:". After posting, it asks whether to delete or
archive the thread.
