# agentd: Azure DevOps on behalf of a person

> Decided 2026-10-09. Azure DevOps has no "create this PR as user X": the PR's **Created by** is whoever's token made the
> call. So agentd acts **as a person** by using that person's own delegated token, obtained once through a sign-in in
> the Web UI. Everything else stays on agentd's own identity (PAT, `az login` or a service principal).

---

## 1. Whose name, for what

| Action | Under whose name |
|---|---|
| A job's PR, its comments and replies (fix rounds), and the job's work item comments and state changes | the work item's **Assigned To**, when that person has connected |
| `!review` findings and re-checks posted to a PR | whoever ran **`!review`** (matched by their agentd user's email), when connected |
| Polling and reading work items, `!chat`'s tools, repositories, pipelines, the wiki | always agentd's own identity |
| A job's **commits** (author and committer) | the work item's **Assigned To**, when connected and agentd has an email for them (§1.1) |
| Git pushes | unchanged: SSH with agentd's key |

**Fallback:** if that person hasn't connected, or their sign-in was revoked or expired, agentd uses its own identity and
says once in the thread: "🔗 Connect your Azure DevOps (Settings → Your Azure DevOps) to have this under your name."
A delegated call is never retried as agentd after it reached Azure DevOps (no duplicate PRs or comments): only a token
that can't be obtained falls back.

### 1.1 Commits

When a job's worktree is made (the job starts, resumes, or its hand-off recreates it), agentd looks up the work item's
Assigned To and sets that worktree's own `git config --worktree user.name/email`, so every commit made there (by the
agent, whichever runner, or by agentd) is theirs. The name and email are what the person set in **Your Azure DevOps**,
otherwise their profile's display name and sign-in email. Nobody connected, or no email: the worktree's setting is
removed, so commits are agentd's (`Agentd:Git:CommitName/CommitEmail`), and the job's thread **asks** the assignee once:
"👤 … add your Azure DevOps (a personal access token, or Connect with Microsoft) in Settings → Your Azure DevOps …".
That ask also covers the PR (no 🔗 note later). Commits made before they connect stay agentd's; the next start or
resume picks the person up.

Per-worktree config needs `extensions.worktreeConfig` on the managed clone. agentd turns it on at the next fetch, moving
`core.bare` into the clone's own `config.worktree` first (otherwise every worktree would think it's bare); the steps are
ordered so running worktrees keep working throughout.

## 2. Connecting (once per person)

**Prerequisites (an admin, once):**
- the organization is connected to a Microsoft Entra tenant;
- an app registration (the same one as the service principal works) with a **web redirect URI**
  `<PublicOrigin>/bff/ado/callback`, the delegated permission **Azure DevOps → `user_impersonation`**, and a client
  secret.

**The flow** (OAuth 2.0 authorization code with PKCE, a confidential client):
1. **Settings → Your Azure DevOps → Connect** calls `GET /bff/ado/connect`. agentd stores a one-time `state` and the
   PKCE verifier in the user's session cookie (Data Protection), then redirects to
   `https://login.microsoftonline.com/<tenant>/oauth2/v2.0/authorize` with scope
   `499b84ac-1321-427f-aa17-267ca6975798/user_impersonation offline_access`.
2. The person signs in and consents. Entra redirects to `/bff/ado/callback?code=…&state=…`.
3. agentd checks `state`, redeems the code at the token endpoint (with the verifier and the client secret), then calls
   `GET https://dev.azure.com/<org>/_apis/connectionData` with the access token to learn the person's **Azure DevOps
   identity** (`authenticatedUser.id`, its unique name and display name).
4. It stores the **refresh token**, encrypted, keyed by that identity id, plus the agentd web login that connected it.
   **Disconnect** deletes the row; revoking the app's consent in Entra has the same effect, seen at the next refresh.

### 2.1 Or a personal access token

Without the Entra app (or by choice), a person pastes a **PAT** in Settings → Your Azure DevOps (`POST
/api/me/ado-connections/pat`), with **Code (Read & write)** and **Work Items (Read & write)** for the organization.
agentd checks it with `connectionData` (Basic auth; an unknown token answers as *Anonymous* and is refused), then stores it
encrypted in the same row (`kind = 'Pat'`), replacing a Microsoft sign-in for that identity. Calls as the person send it as
Basic auth; there's no refresh. The field is write-only: the token is never sent back.

The person can also set their **commit name and email** (`PUT /api/me/ado-connections/{id}/commit-author`, blank: from
the profile); reconnecting keeps them.

Only the person who connected (their web login) sees and removes their connection; Admins see who is connected, never
any token.

## 3. Storage

`agentd.ado_user_connections`, one row per Azure DevOps identity:

| Column | |
|---|---|
| `ado_identity_id uuid` PK | the Azure DevOps identity id, the same id a work item's `System.AssignedTo` carries |
| `unique_name text` | the sign-in name (email/UPN), for matching a `!review` requester |
| `display_name text` | for the UI |
| `web_login text` | the agentd web login that connected it (who may disconnect it) |
| `refresh_token bytea` | the secret (a refresh token, or a PAT per `kind`), **encrypted** with ASP.NET Core Data Protection (purpose `agentd.ado-user-tokens`); the database never sees it in clear |
| `kind text` | `OAuth` (Microsoft's sign-in) or `Pat` |
| `commit_name`, `commit_email text` | what goes on their commits; null: from the profile |
| `status text` | `Connected`, or `Failed` with `last_error` (a refresh was refused: revoked, expired, password reset) |
| `connected_at`, `refreshed_at` | |

Routines (`Routines/ado/ado_user_connection_routines.sql`): `ado_connection_upsert` (connecting again replaces the
token: idempotent and safe for parallel callers), `ado_connection_find_by_identity`, `ado_connection_find_by_unique_name`
(case-insensitive), `ado_connection_list_by_web_login`, `ado_connection_store_token` (Entra rotates refresh tokens on
use), `ado_connection_mark_failed`, `ado_connection_delete` (only by its own web login), `ado_connection_set_commit_author`
(only by its own web login).

Access tokens are never stored: they're cached in memory per identity until five minutes before expiry.

## 4. Where it lives

| Layer | Pieces |
|---|---|
| Application | port `IAdoUserConnections` (the store), port `ITokenProtector` (encrypt / decrypt), `AdoUserTokens` (refresh → access token, mark failed, the fallback decision) |
| Infrastructure.Persistence | `AdoUserConnectionStore` over the routines |
| Infrastructure.AzureDevOps | the Entra token endpoint client (code redemption, refresh), `connectionData`, and the per-call identity: the Azure DevOps clients take "as whom" for the actions in §1 |
| Host | `ITokenProtector` over Data Protection |
| Bff | `/bff/ado/connect`, `/bff/ado/callback`, `GET/DELETE /api/me/ado-connection` |
| Web | Settings → **Your Azure DevOps**: Connect, "Connected as … · Disconnect", or the error to fix |

## 5. Delivery

1. The token store: this design, the migration, the routines with integration tests (including parallel callers), the
   store, and the protector.
2. Connect / Disconnect: the OAuth flow, the BFF endpoints and the settings section.
3. Using the tokens:
   - **3a, jobs:** `AdoUserTokens` (refresh, rotate, cache, mark failed), `AdoActor` (an async-local "as whom" scope that
     agentd's auth provider honours: `ActingAsAuthProvider`), and `AdoOnBehalf` (the work item's Assigned To, or
     agentd's own with the thread note). Claiming and publishing run inside the scope.
   - **3b, `!review`:** findings and re-checks under the person who ran it.
