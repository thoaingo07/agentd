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
| Git pushes | unchanged: SSH with agentd's key |

**Fallback:** if that person hasn't connected, or their sign-in was revoked or expired, agentd uses its own identity and
says once in the thread: "🔗 Connect your Azure DevOps (Settings → Your Azure DevOps) to have this under your name."
A delegated call is never retried as agentd after it reached Azure DevOps (no duplicate PRs or comments): only a token
that can't be obtained falls back.

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
| `refresh_token bytea` | **encrypted** with ASP.NET Core Data Protection (purpose `agentd.ado-user-tokens`); the database never sees it in clear |
| `status text` | `Connected`, or `Failed` with `last_error` (a refresh was refused: revoked, expired, password reset) |
| `connected_at`, `refreshed_at` | |

Routines (`Routines/ado/ado_user_connection_routines.sql`): `ado_connection_upsert` (connecting again replaces the
token: idempotent and safe for parallel callers), `ado_connection_find_by_identity`, `ado_connection_find_by_unique_name`
(case-insensitive), `ado_connection_list_by_web_login`, `ado_connection_store_token` (Entra rotates refresh tokens on
use), `ado_connection_mark_failed`, `ado_connection_delete` (only by its own web login).

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
