# Troubleshooting

Start with **`agentd doctor`**. It checks:
- the configuration and the database, including pending migrations;
- Azure DevOps, git, Claude (with a live test prompt) and Discord.

Each finding is ✅ / ⚠️ / ❌ and comes with a hint. `agentd doctor --repo <name>` also checks that the repository's
toolchains (dotnet, node, …) are installed.

Logs: `agentd daemon logs` (add `-f` to follow).

## A job seems stuck

- In its thread, ask anything (or send `!status`). agentd answers right away with what the agent is doing, for how
  long, and the last lines of a running command's output.
- After `Jobs:StuckAfter` (5 minutes) with no activity, the thread gets a "no activity" warning.
- A command that hangs is stopped after `Claude:CommandTimeout` (10 minutes).
- `!pause` stops the agent and keeps everything; `!resume` continues. `!cancel` stops the job for good (`!retry`
  resumes it).

## The agent keeps asking for permission

- Answer **2** (this job) or **3** (always in this repository) instead of **1**, so the same command isn't asked again.
- Add commands your team always allows to `Claude:AllowedTools`, or switch to `Jobs:PermissionMode` `Auto`
  ([Safety](safety.md)).

## "Usage limit reached"

Your Claude subscription hit its limit. agentd pauses the job and resumes it **by itself** when the limit resets (or
after `Jobs:UsageLimitBackoff`, 30 minutes, when the reset time isn't known). Nothing to do.

## A message got no reply

- **You're not in `Users`:** messages from unknown people are ignored silently. Add your Discord user ID under
  `Identities` ([Configuration](configuration.md)).
- **Wrong place:** commands like `!run` go in the channel; replies to a job, idea or review go **in its thread**.
- **Wrong time:** a job that hasn't started, has failed, or is paused tells you why it didn't take the message, and
  what to do (`!retry`, `!resume`).
- **Still nothing:** check `agentd daemon logs`, and that Discord passes `agentd doctor`.

## A work item isn't picked up

- **Tag:** it needs the `ai-workflow` tag (`Jobs:Tag`).
- **Repository:** it must match a registered repository by its `repo:<name>` tag or area path. If no repository
  matches, agentd says so once in a comment on the work item. `agentd run <id> --repo <name>` starts it anyway.
- **Already running:** a work item that already has an active job isn't started twice.
- **Scheduler:** `Scheduler:Enabled` must be on, and at most `Scheduler:MaxConcurrent` (2) jobs run at once. Others
  wait in the queue.

## The web UI doesn't open

- Open the daemon's URL (default `http://127.0.0.1:7780`), through an SSH tunnel from another computer:
  `ssh -L 7780:127.0.0.1:7780 <server>`.
- agentd refuses to start if `Web:Urls` isn't a loopback address, and in `Auth:Mode` `None` it refuses requests that
  came through a proxy. To use a proxy, set up Cloudflare Access ([Configuration](configuration.md#web-access)).
- The port is taken: change `Web:Urls`, or, with Docker, the published ports in `compose.yaml`.

## Disk filling up

- Finished jobs' worktrees are removed after `Jobs:RetainFinishedWorktrees` (1 day); failed ones after
  `Jobs:RetainFailedWorktrees` (3 days).
- The web UI's header and `!status` show disk, CPU and RAM for the machine and for each job.
