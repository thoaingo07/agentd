# Artifacts

> Decided 2026-10-10. Claude Code's own Artifact tool isn't available in headless runs (`claude -p`, checked with
> 2.1.296), so agentd publishes the pages itself and serves them to anyone who can open its Web UI.

An **artifact** is an HTML page an agent publishes: a report, a diagram, an explainer, a comparison table. People open
it in the browser at **`/artifacts/<slug>-<hash>.html`** and find every page under **Artifacts** in the Web UI.

## 1. Publishing

- **Tool:** `publish_artifact(title, html)` on agentd's MCP server (`ArtifactTools`), for **jobs** and **`!chat`**
  sessions (any agent token, `McpHosting.AgentPolicy`). The source (the job or the chat) comes from the bearer token,
  never from an argument. Ideas and review Asks have no MCP server yet, so they can't publish.
- **HTML:** a whole document, or only the body (agentd wraps it in a minimal page with the title). Up to 2 MB.
- **Name:** a slug of the title (lower-case ASCII words and dashes, at most 48 characters) plus 8 random hex digits, so
  links can't be guessed. Publishing **again with the same title from the same job or chat** replaces the page and keeps
  its name (`ux_artifacts_source_title`, `NULLS NOT DISTINCT`); parallel publishes of one title make one page.
- **Reply:** the link (`Agentd:Web:PublicOrigin` + the path), to share in chat or the PR.
- A job's page is announced on its event stream (`artifact.published`, name and title only): the transcript shows
  "Artifact published: <title>" with a link.

## 2. Storage

`agentd.artifacts` (name, title, html, job_id or chat_id, author, created/updated), through `artifact_publish`,
`artifact_get` and `artifact_list` (newest first, without the HTML).

## 3. Serving and isolation

- `GET /artifacts/{name}.html` needs the same sign-in as `/api` (everyone who can open the Web UI can read every
  artifact). `GET /api/artifacts` lists them.
- The page is agent-written HTML on agentd's own origin, so it gets **its own CSP** (`SecurityHeaders.ArtifactPolicy`)
  instead of the app's:
  - `sandbox allow-scripts allow-popups allow-popups-to-escape-sandbox`, without `allow-same-origin`: the page runs
    as an **opaque origin**. It can't read agentd's cookies or storage, and requests to agentd don't carry the session;
  - `connect-src 'none'` and `form-action 'none'`: it can't send anything anywhere;
  - inline scripts and styles run (`'unsafe-inline'`, `'unsafe-eval'` for chart libraries), and scripts, styles,
    fonts and images may load from `cdnjs.cloudflare.com`, `cdn.jsdelivr.net` and `unpkg.com` (plus Google Fonts);
  - `frame-ancestors 'none'`: it can't be framed.
- The app's own Artifacts list (`/artifacts`, no file) keeps the app's policy (`SecurityHeaders.IsArtifact`).
- An E2E test (`e2e/artifacts.spec.ts`) checks this in Chromium and Firefox: the page's script runs, while cookies,
  storage and `/api` are blocked.

## 4. Later

- Ideas and review Asks publishing (they need an MCP token like `!chat`'s).
- Viewers by email domain (e.g. `*@ermsystem.com`) and a read-only role; today, whoever can sign in sees everything.
