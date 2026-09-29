# Phase 5 — SSO (Microsoft Entra ID, Google) + roles — Tasks

Task index for [Phase 5 — SSO (Microsoft Entra ID, Google) + roles](../README.md).

> **Detail files are written just in time, when this phase starts** (after the previous phases are built). They follow the same template as Phases 0–4, and are based on what earlier phases actually delivered, so they don't go stale.

| ID | Task | Status |
|---|---|---|
| T5.1 | Auth options + validation (a provider must be enabled for `Sso`; secrets present). | ☐ detail pending |
| T5.2 | Cookie + OIDC scheme configuration. Integration tests against a **mock OIDC server** (Testcontainers or an in-process stub). | ☐ detail pending |
| T5.3 | `ExternalLoginMapper` + the users and identities repository + linking rules, with unit tests (email reuse, an unverified email, a wrong tenant or domain). | ☐ detail pending |
| T5.4 | The session store + validator + revoke endpoint. | ☐ detail pending |
| T5.5 | Data Protection → PostgreSQL. | ☐ detail pending |
| T5.6 | Policies on the endpoint groups and the hub; role checks for chat commands. | ☐ detail pending |
| T5.7 | UI: LoginView, 401 → `/login?returnUrl`, `access_denied` message, role gating, UsersView. | ☐ detail pending |
| T5.8 | The deploy guide (Entra, Google, reverse proxy, `ForwardedHeaders`). | ☐ detail pending |
