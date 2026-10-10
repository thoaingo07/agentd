# The web dashboard

The dashboard runs inside agentd, at **http://127.0.0.1:7780** on the server. To reach it from your laptop, use
`ssh -L 7780:127.0.0.1:7780 my-server`, or Cloudflare Tunnel + Access (see [Configuration → Web access](configuration.md#web-access)).
It updates live; the dot in the header shows whether it's connected.

## The header

- **The navigation:** Dashboard · History · Ideas · Reviews · Settings.
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
  - state, **the current step** (e.g. `🔨 Implement · deepseek · deepseek-flash`), phase, branch, Claude session id,
    usage, and the job's CPU / RAM / disk;
  - links to the work item, the chat threads and the PR;
  - **Pause**, **Resume**, **Cancel** and **Retry**.
- **🔐 Permission banner:** when the agent asks for a command, answer with the same four choices as in chat.
- **Tabs** (`1` `2` `3`):
  - **Transcript:** everything the agent said and did, live. Tool calls fold open, and long outputs load on demand.
    Each step's start is a row (under **state**), so you can see which model did what.
  - **Diff:** what the branch changes, refreshed as the agent edits files.
  - **Details:** plan status, estimate, attempts, errors, threads.
- **The message box** sends a message to the agent, like replying in the thread.

## History

All jobs, newest first. Search by title or `WI-1234`, and filter by state, repository and date.

## Work item

Everything about one work item on one page (the dashboard and History open it; an old `/jobs/<id>` link lands here
with that run picked). The header has the run's **Pause / Resume / Cancel / Retry**; with several runs, pick one
("Run 1", "Run 2 (rework)") and the run tabs follow it. Tabs (keys 1–6):
- **Transcript:** the agent's text and tool calls, steps, questions, and a box to message the agent. It opens on the
  newest part, live while the run is active; scroll up for earlier parts;
- **Diff:** the run's branch against its base;
- **Timeline:** every step of every run;
- **Conversation:** chat both ways, with the provider;
- **Pull requests**;
- **Details:** the run's attempts, plan, fix rounds and last error, and the estimate vs. the actual with usage.

If the work item came from an idea, the header links to it ("💡 born from idea #N").

## Ideas

Every `!idea`: its status, repository, author, message and draft counts, and the work items it created. An idea's
page shows its model and effort, the latest drafts as stories with their tasks, and the whole conversation.
Brainstorming itself happens in chat.

## Reviews

**Reviews → Open pull requests** lists the registered repositories' active PRs, newest first, and refreshes itself
every minute (or **Refresh**). **Review** starts a review of one; when you already reviewed it, **My review** reopens
yours. A repository agentd can't read is named with the reason.

**Reviews → New review**: pick a repository, then a **pull request**, a **pushed branch** (against its base, or
another branch you name) or **two commits**. agentd fetches it with its own access, pins the commits, and its
reviewer (the `review` step's model) looks for what can break the app or slow it down, with a fix for each.

The review page:
- the **files** on the left, the **diff** in the middle, and each **finding at its line** (🔴 can break the app,
  🟠 performance) with **Keep**, **Edit** (your wording) or **Drop**;
- **click a line number** to comment there, or **Ask** about that code ("why is this here?"); the whole change has
  its own comment and Ask box at the side. Answers come from the code at the review's head, naming files and lines,
  and show under **Questions** ("thinking…" until then). Each question is a conversation: **Ask a follow-up** under
  its answer, and the agent remembers what you discussed;
- "Reviewing…" while the reviewer works: the findings appear when it's done, and you can read and comment meanwhile.

**Send** (when it's Ready): only the findings you kept (edited ones in your words) and your comments go.
- **Post to the PR** (a PR's review): one thread per finding at its line, one per comment, and a main message with
  the list. It's under **your name** when you've connected your Azure DevOps (Settings → Your Azure DevOps),
  otherwise agentd's.
- **Fix it (push to `<branch>`)** (a PR's or a branch's review): after you confirm, an agent fixes what you kept on
  the reviewed commit, and agentd commits (as agentd) and **pushes to that same branch**: the PR picks it up. Then it
  reviews the new head, and the page links **round 2**, where you can keep fixing. The push is never forced: if
  someone pushed to the branch meanwhile, it's refused and the page says to review again. On a PR, a short note says
  what was fixed (under your name when you've connected your Azure DevOps).
- **Copy as text**: the same list as text, e.g. for an agent or a message.

**My reviews** lists the ones you started.

## Settings

- **Configuration** (Admins): the same pages as the setup wizard, to change settings after setup. Each has a
  **Test** button, and secrets show only "set · updated … by …". Saved changes apply after `agentd daemon restart`.
  - **Health**: every area checked with its saved settings (including a tiny Claude test prompt).
  - **Database**, **Azure DevOps**, **Git access** (agentd's SSH key), **Claude**, **Chat**, **Repositories**.
- **Your Azure DevOps** (everyone): **Connect with Microsoft** once, or paste a **personal access token** (Code and
  Work Items, read & write), and agentd acts as you in Azure DevOps: commits, PRs, comments and updates for work items
  assigned to you, and the `!review` findings you post, show your name instead of agentd's. The token is stored
  encrypted and never shown again. It shows who you're connected as and the name and email your commits get
  (**Change** to set your own), flags a sign-in that needs reconnecting, and **Disconnect** removes it. Connect with
  Microsoft needs the Entra app an Admin sets up (see [Getting started](getting-started.md#3-azure-devops)); a token
  doesn't. When a job starts for a work item assigned to someone who hasn't done this, the job's thread asks them to.
- the theme;
- your session and the live connection;
- the daemon's configuration (read-only: tag, poll interval, concurrency, plan approval, review loop, chat,
  repositories);
- **Remembered permissions**: "for this job" and "always in this repo" approvals. Admins can **Revoke** them.
