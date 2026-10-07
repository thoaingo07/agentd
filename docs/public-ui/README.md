# agentd user guide

agentd runs coding agents (Claude Code) on your **Azure DevOps work items**. Tag a work item and agentd:
1. picks it up;
2. talks to you in a **Discord thread** while it plans and implements;
3. opens a **pull request**;
4. fixes what reviewers ask for.

You can also **brainstorm ideas into work items** and **review any PR** from chat. Everything is visible in a
**web dashboard**.

This guide is for people who **use and run** agentd. If you develop agentd itself, see [`docs/architect`](../architect/README.md)
and [`docs/plan`](../plan/README.md).

## Contents

| Guide | What's in it |
|---|---|
| [Getting started](getting-started.md) | Install agentd (release, Docker or source), connect Azure DevOps, Claude and Discord, add a repository, check it with `doctor` |
| [Working with jobs](jobs.md) | The job cycle: tag → plan → implement → pull request → review → merge → hand-off → close-out |
| [Chat commands](chat.md) | Everything you can type in Discord, in the channel and in a job's thread |
| [Brainstorming ideas](ideas.md) | `!idea`: turn a rough idea into User Stories and Tasks |
| [Reviewing pull requests](reviews.md) | `!review`: an agent reviews a PR; you choose what gets posted |
| [The web dashboard](web-ui.md) | Dashboard, sessions, history, work items, ideas, settings |
| [Command line](cli.md) | Every `agentd` command on the server |
| [Configuration](configuration.md) | `agentd.json`, secrets, and the settings you're most likely to change |
| [Safety and permissions](safety.md) | What agents may and may not do, permission requests, auto mode, secrets |
| [Troubleshooting](troubleshooting.md) | Common problems and how to fix them |

## The short version

```text
Azure DevOps work item, tagged ai-workflow
      │  agentd polls every minute
      ▼
💬 a Discord thread opens: the agent clarifies and posts a plan → you reply 1 (approve)
      ▼
🔧 it implements and verifies in its own git worktree, telling you what it's doing
      ▼
📤 it pushes a branch and opens a pull request
      ▼
🔁 review comments (in the PR or the thread) start fix rounds
      ▼
✅ you merge → 🎓 knowledge hand-off → 🧹 "delete this thread?"
```

The thread stays the place to talk: ask "what's the progress?" anytime, answer the agent's questions, and send
instructions. Type `!help` in Discord for the full list of commands.
