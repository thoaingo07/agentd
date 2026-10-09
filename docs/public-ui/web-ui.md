# The web dashboard

The dashboard runs inside agentd, at **http://127.0.0.1:7780** on the server. To reach it from your laptop, use
`ssh -L 7780:127.0.0.1:7780 my-server`, or Cloudflare Tunnel + Access (see [Configuration → Web access](configuration.md#web-access)).
It updates live; the dot in the header shows whether it's connected.

## The header

- **The navigation:** Dashboard · History · Ideas · Settings.
- **"N waiting":** jobs waiting for you. Click it to see them.
- **The machine's CPU, free RAM and free disk** (on phones, CPU and free RAM). It's marked ⚠️ when disk or memory
  runs low. Click it for the **Host** panel at the top of Settings: CPU with cores and load, RAM, swap, every disk
  (and which holds agentd's home), and uptime.
- **Live / Reconnecting:** the connection; it catches up by itself after a drop.
- **The theme toggle:** system, light or dark.

## Dashboard

- **Stats:** how many jobs are in each state.
- **Active jobs:** state, work item, repository, branch, phase, CPU/RAM (on wide screens) and elapsed time.
  - Jobs waiting for you are highlighted, and 🔐 *N* marks open permission requests.
  - Each row has buttons for the work item, the PR, Retry and Cancel.
- **Run work item:** start a work item by id, like `!run`.
- **Keyboard:** `j` / `k` move between rows, `Enter` opens one.

## Session (a job's page)

- **The header:**
  - state, phase, branch, Claude session id, usage, and the job's CPU / RAM / disk;
  - links to the work item, the chat threads and the PR;
  - **Pause**, **Resume**, **Cancel** and **Retry**.
- **🔐 Permission banner:** when the agent asks for a command, answer with the same four choices as in chat.
- **Tabs** (`1` `2` `3`):
  - **Transcript:** everything the agent said and did, live. Tool calls fold open, and long outputs load on demand.
  - **Diff:** what the branch changes, refreshed as the agent edits files.
  - **Details:** plan status, estimate, attempts, errors, threads.
- **The message box** sends a message to the agent, like replying in the thread.

## History

All jobs, newest first. Search by title or `WI-1234`, and filter by state, repository and date.

## Work item

Every run of one work item, with these tabs:
- **Timeline:** every event of every run;
- **Conversation:** chat both ways, with the provider;
- **Activity:** what the agents did;
- **Pull requests**;
- **Plan & usage:** the estimate vs. the actual.

If the work item came from an idea, the header links to it ("💡 born from idea #N").

## Ideas

Every `!idea`: its status, repository, author, message and draft counts, and the work items it created. An idea's
page shows its model and effort, the latest drafts as stories with their tasks, and the whole conversation.
Brainstorming itself happens in chat.

## Settings

- **Configuration** (Admins): the same pages as the setup wizard, to change settings after setup. Each has a
  **Test** button, and secrets show only "set · updated … by …". Saved changes apply after `agentd daemon restart`.
  - **Health**: every area checked with its saved settings (including a tiny Claude test prompt).
  - **Database**, **Azure DevOps**, **Git access** (agentd's SSH key), **Claude**, **Chat**, **Repositories**.
- the theme;
- your session and the live connection;
- the daemon's configuration (read-only: tag, poll interval, concurrency, plan approval, review loop, chat,
  repositories);
- **Remembered permissions**: "for this job" and "always in this repo" approvals. Admins can **Revoke** them.
