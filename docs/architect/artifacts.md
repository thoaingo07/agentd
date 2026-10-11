# Artifacts

> Decided 2026-10-10. Claude Code's own Artifact tool isn't available in headless runs (`claude -p`, checked with
> 2.1.296), so agentd publishes the pages itself and serves them to anyone who can open its Web UI.

An **artifact** is an HTML page an agent publishes: a report, a diagram, an explainer, a comparison table. People open
it in the browser at **`/artifacts/<slug>-<hash>.html`** and find every page under **Artifacts** in the Web UI.

## 1. Publishing

- **Tool:** `publish_artifact(title, html)` on agentd's MCP server (`ArtifactTools`), for **every agent** (decided
  2026-10-10): jobs and `!chat` with their own tokens, and every other turn (ideas, `!review` threads, a review page's
  reviewer, its questions and Fix it, follow-ups after the merge, a watched PR's fix rounds) with an **artifact-only
  token** (`IMcpTokenIssuer.IssueArtifacts`, run through `ArtifactTurn`): issued for the turn, revoked when it ends,
  several alive at once for parallel turns of one source, and it lists and calls nothing but `publish_artifact`. The
  source (`job`, `chat`, `idea`, `review`, `review-session`, `watch`) and its id come from the bearer token, never from
  an argument.
- **When asked, just publish:** every agent's instructions (jobs' `SystemPromptRules`, threads' `ArtifactRule`) say to
  publish an artifact right away when someone asks for one, without asking first, and to reply with the link.
- **HTML:** a whole document, or only the body (agentd wraps it in a minimal page with the title). Up to 2 MB.
- **Name:** a slug of the title (lower-case ASCII words and dashes, at most 48 characters) plus 8 random hex digits, so
  links can't be guessed. Publishing **again with the same title from the same source** replaces the page and keeps
  its name (`ux_artifacts_source_title` on source, source id and title); parallel publishes of one title make one page.
- **Reply:** the link (`Agentd:Web:PublicOrigin` + the path), to share in chat or the PR.
- A job's page is announced on its event stream (`artifact.published`, name and title only): the transcript shows
  "Artifact published: <title>" with a link.

## 2. Storage

`agentd.artifacts` (name, title, html, source and source_id, author, created/updated, expired_at), through
`artifact_publish`, `artifact_get`, `artifact_list` (live pages, newest first, without the HTML) and `artifact_expire`.
The HTML is kept in the database on purpose: when the Web UI is hosted apart from the daemon (e.g. in the cloud), the
database is what both share, so the web serves pages without the daemon's disk.

**S3-compatible storage** (Cloudflare R2, decided 2026-10-10): with `Agentd:Artifacts:S3` configured (`Url` = the S3
API URL with the bucket as its path, e.g. `https://<account>.r2.cloudflarestorage.com/agentd`, plus `AccessKeyId` and
`SecretAccessKey` from the encrypted secret store), new pages are uploaded to `artifacts/<name>.html` and the row keeps
only the metadata (`storage = 's3'`, no HTML). The row stays: it lists pages, keeps one link per source and title
(race-safe), expires them, and links them to their agent. `/artifacts/<name>.html` still serves them through agentd (the
same sign-in and sandbox CSP), so the bucket stays private. Earlier pages stay in the database (`storage = 'database'`).
`S3ArtifactBlobs` sets what R2 needs: path-style URLs, the region `auto`, no default checksums, no streaming payload
signature (HTTPS only). An upload that fails is an error to the agent ("publish it again"); an object missing later
answers 502. Expiring an S3 page deletes its object too.

**Retention** (decided 2026-10-10): a page expires `Agentd:Artifacts:RetentionDays` (default 30; 0 = never) after its
last update. The hourly sweep (`WorktreeSweepWorker`) calls `artifact_expire`, which drops the HTML and keeps a
tombstone (name, title, `expired_at`). Its link then answers **410 Gone** with "This artifact expired on <date>. Ask
the agent to publish it again." Publishing the same title again from the same job or chat brings it back under the
same link. Expired pages aren't listed.

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

- A **Keep** toggle that exempts a page from expiry, if links in PRs need to outlive the retention.
- Viewers by email domain (e.g. `*@ermsystem.com`) and a read-only role; today, whoever can sign in sees everything.
