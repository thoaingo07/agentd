# T1b.12 — Setup wizard, Settings pages and Health

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1b | T1b.11 | L | web |

## Goal
The `ClientApps/setup` SPA (8 steps with Test buttons) and, in the dashboard, Settings pages per area plus a
Health page showing the doctor report.

## Files
- `src/Agentd.Web/ClientApps/setup/*`: create. Its own entry (`main.ts`), router and stores; shared `Ag*` components.
- `src/Agentd.Web/ClientApps/dashboard/views/settings/*`: create. Access, Azure DevOps, Git, Models, Chat,
  Repositories, Health.

## Implementation
1. Secret fields are write-only inputs ("set · updated … by …", Replace, Remove).
2. The public SSH key has a copy button and links to where to add it.
3. Same CSP and a11y rules (the T3.12 suite covers the wizard pages).

## Tests
- vitest for each step's store and form; Playwright: the wizard happy path against the E2E host.

## Done when
- [ ] A fresh install is configured end to end in the browser.
