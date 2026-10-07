# Chat commands

Commands start with **`!`** in Discord. `!help` shows them all, with the job cycle.

## In the agentd channel

| Command | What it does |
|---|---|
| `!list` | active jobs |
| `!run <work item id>` | start a work item now, even without the `ai-workflow` tag |
| `!idea [--repo r] [--model m] [--effort e] <text>` | brainstorm an idea into work items, in a new 💡 thread ([Ideas](ideas.md)) |
| `!review <PR url or id> [instructions] [--repo r] [--focus f] [--model m] [--effort e]` | review a pull request, in a new 🔍 thread ([Reviews](reviews.md)) |
| `!repo list` | registered repositories and how work items match them |
| `!repo add <clone url> [--name n] [--tag t] [--base b] [--area-path p]` · `!repo remove <name>` | register or remove a repository (Admins) |
| `!help` | everything agentd can do |

## In a job's thread

| Command | What it does |
|---|---|
| `!status` | state, branch, PR, what the agent is doing now (with the latest output of a running command), CPU/RAM/disk, and the machine's totals |
| `!logs` | the agent's recent transcript |
| `!pause` · `!resume` | stop for now; continue later in the same session (`!cancel` ends it) |
| `!cancel` | stop the job for good |
| `!retry` | run a failed or cancelled job again |
| `!handoff` | start the knowledge hand-off (after the PR is merged) |
| `!approve [request] [once\|job\|always]` · `!deny [request]` | answer a permission request |

**Anything that isn't a command goes to the agent.**
- **Answers to its questions** can be the option's number (`1`, `2`, …) or your own words.
- **Plan approval:** reply `1` or `approve`, or describe what to change.
- **"What's the progress?"** gets the status right away.
- **While the agent is busy,** your message is queued and read at its next step.
- **While the PR is in review,** a message starts a fix round, like a review comment.
- **After the merge,** you get an answer from the same session, but no more code changes.

## Permission requests

When an agent needs a command outside its allowlist (and agentd isn't in auto mode), the thread asks:

> 🔐 **Permission needed** (request 12): the agent wants to run `npm install --no-audit`
> Reply **1** allow once · **2** allow for this job · **3** always allow in `my-repo` · **4** deny.

- **No answer within 10 minutes is a deny,** and the agent carries on without it.
- **"For this job" and "always" are remembered:** "always" covers that kind of command, so `npm` covers
  `npm install`. Admins can revoke remembered approvals under **Settings → Remembered permissions**.
- See [Safety and permissions](safety.md).

## In an idea's or a review's thread

| Command | What it does |
|---|---|
| `!model <fable\|opus\|sonnet\|haiku\|full name>` | use this model from the next reply (no argument: show the current one) |
| `!effort <low\|medium\|high\|xhigh\|max>` | the thinking effort from the next reply |

## Notices you'll see

- **A heartbeat** every minute while the agent works, replacing the previous one.
- **⚠️ No activity for N min**, or **⚠️ The agent has been running `<command>` for N min**, when it's quiet for 5
  minutes. The second one includes the command's latest output, and `!pause` stops it.
- **⚠️ Usage at 80% / 95%** of the 5-hour or weekly window.
- **⚠️ Low disk / Low memory** on the server.
