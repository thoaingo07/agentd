# T1b.11 — `SetupService` and the setup/settings API

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1b | T1b.2, T1b.10 | L | Application, Bff |

## Goal
The setup and settings use cases, shared by the wizard, Settings and `agentd init`, behind `/api/setup/*`
(setup session) and `/api/settings/*` (Admin), with write-only secrets and an audit trail.

## Files
- `src/Agentd.Application/Setup/*`: create. Use cases per step (database, Azure DevOps, git key, models, chat,
  repositories, review) with a **Test** for each; ports `IConfigWriter` (agentd.json) and `ISecretWriter`.
- `src/Agentd.Host/Configuration/*`: implement the ports (atomic writes, 0600).
- `src/Agentd.Bff/Endpoints/SetupEndpoints.cs`, `SettingsEndpoints.cs`: create.

## Implementation
1. Secrets are write-only: responses carry `{ set: true, updatedAt, updatedBy }`, never values.
2. Every change appends a `settings.changed` event (who, which key, when; never the value).
3. Config changes that need a restart say so; the rest apply live (`IOptionsMonitor` where it's cheap).

## Tests
- Each use case with fakes; endpoints: antiforgery, Admin policy, setup session scope, no secret in any response.

## Done when
- [ ] The wizard and `init` can complete setup through these use cases only.
