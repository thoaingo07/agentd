# Phase 5 — SSO (Microsoft Entra ID, Google) + roles

**Goal:** make agentd safe to reach remotely. Sign-in is server-side OIDC through the BFF, there is
one user directory for the web and chat, and **Viewer / Operator / Admin** are enforced everywhere.

Design refs: [authentication.md](../security/authentication.md) · [Web Security](../security/README.md)

---

## Scope

**In**

- `Auth.Mode = Sso`: cookie auth + two OIDC schemes (`microsoft`, `google`): code + PKCE,
  `ResponseMode = query`, `SaveTokens = false`, per-scheme issuer validation.
- **`ExternalLoginMapper`:**
  - `(issuer, subject)` → the agentd user;
  - linking once by verified email;
  - deny unknown users, with optional `AutoProvision`;
  - Entra `tid` / assignment and Google `hd` / `email_verified` checks;
  - the minimal claims principal.
- **Sessions:**
  - the `user_sessions` table;
  - `OnValidatePrincipal` revocation check every 5 minutes;
  - idle timeout and absolute lifetime;
  - Data Protection keys in PostgreSQL.
- BFF: `/bff/login?provider=`, `/bff/logout`, `/bff/user`, `/bff/providers`; `returnUrl` validation.
- **Authorization policies** on every `/api` endpoint and the hub. **Chat commands** are checked
  against the same roles in `HandleInboundMessage`.
- **UI:** a `/login` page with Microsoft and Google buttons (static SVGs, no IdP JavaScript), the
  `session` store with `hasRole`, role-based show/hide, a **Users** page (Admin) with users,
  identities and active sessions (with revoke).
- `ForwardedHeaders` configuration for a TLS reverse proxy; binding to non-loopback addresses is
  allowed only in SSO mode.
- A setup guide for the Entra and Google app registrations.

**Out:** MFA step-up for Admin actions, which is optional and deferred to Phase 10.

---

## Tasks

1. Auth options + validation (a provider must be enabled for `Sso`; secrets present).
2. Cookie + OIDC scheme configuration. Integration tests against a **mock OIDC server**
   (Testcontainers or an in-process stub).
3. `ExternalLoginMapper` + the users and identities repository + linking rules, with unit tests
   (email reuse, an unverified email, a wrong tenant or domain).
4. The session store + validator + revoke endpoint.
5. Data Protection → PostgreSQL.
6. Policies on the endpoint groups and the hub; role checks for chat commands.
7. UI: LoginView, 401 → `/login?returnUrl`, `access_denied` message, role gating, UsersView.
8. The deploy guide (Entra, Google, reverse proxy, `ForwardedHeaders`).

## Exit criteria (the demo)

- Through the HTTPS reverse proxy: sign in with **Microsoft** as an Admin and with **Google** as a
  Viewer. The Viewer can see everything but gets **403** on cancel/retry and doesn't see those
  buttons.
- An unknown Google account from another domain is denied with a clear message.
- Revoking a session from the Users page logs that browser out within 5 minutes.
- A Viewer's chat reply to a waiting job is ignored with a notice, and an Operator's reply resumes it.
- Playwright: the login flow against the mock IdP; still zero CSP violations; no JS-readable cookies.

## Risks / open questions for review

- **Move this before Phase 4?** Only needed if remote access is required earlier.
- **Role source for Microsoft:** the agentd directory (default) or Entra app roles?
- **AutoProvision:** off (default). Should anyone in your tenant or domain get Viewer automatically?
- **Hosting:** where will the TLS reverse proxy or tunnel run (Caddy/nginx, Cloudflare Tunnel, Tailscale)?
