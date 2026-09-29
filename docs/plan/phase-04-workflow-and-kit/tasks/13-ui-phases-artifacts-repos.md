# T4.13 — UI: phase stepper, Artifacts tab, gate buttons, Repos page

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 4 | T4.6, T4.10, T4.11, Phase 3 (BFF + Web UI v1) | M | `Agentd.Bff`, `src/Agentd.Web/` |

## Goal
Make the workflow visible and controllable in the browser: the current phase with its loop counts,
every artifact, gate approve/reject, and a **Repos** page showing each repo's kit status with an
**Initialize kit** button ([UI §4.2](../../../ui/README.md)).

## Files
- `src/Agentd.Bff/Endpoints/JobEndpoints.cs` — modify: add `phases[]` and `openGate` to the job view model; `GET /api/jobs/{id}/artifacts`, `GET /api/jobs/{id}/artifacts/{artifactId}`; `POST /api/jobs/{id}/gates/{phase}/approve|reject` (`{ reason? }`).
- `src/Agentd.Bff/Endpoints/RepoEndpoints.cs` — create: `GET /api/repos` (kit status), `POST /api/repos/{repo}/kit/init` (`{ bootstrap: bool }`), `POST /api/repos/{repo}/kit/validate`.
- `src/Agentd.Web/ClientApps/dashboard/components/PhaseStepper.vue` — create.
- `src/Agentd.Web/ClientApps/dashboard/components/GateBanner.vue` — create: approve / reject (with a reason modal).
- `src/Agentd.Web/ClientApps/dashboard/components/ArtifactViewer.vue` — create: markdown-lite render of the artifact.
- `src/Agentd.Web/ClientApps/dashboard/views/SessionView.vue` — modify: stepper under the header, a 4th tab **Artifacts**, and the gate banner.
- `src/Agentd.Web/ClientApps/dashboard/views/ReposView.vue` + `src/Agentd.Web/ClientApps/dashboard/stores/repos.ts` — create.
- `src/Agentd.Web/ClientApps/dashboard/router.ts`, navbar — modify: the `/repos` route.

## Implementation
1. **View models:**
   - `JobPhaseVm { phase, status: pending|active|done|skipped, loopCount, startedAt, completedAt }`;
   - `OpenGateVm { phase, openedAt, artifactId }`;
   - `RepoVm { name, baseBranch, kit: { present, version, valid, errors[], initPrUrl?, customizedFiles? } }`.

   `customizedFiles` is computed from the `baseline` hashes; it matters mostly for Phase 10.
2. **Live updates:**
   - `phase.started` / `phase.completed` / `job.waiting` events update the `jobs` store (phases,
     `openGate`);
   - the `repos` store refetches on the `kit.*` events emitted by `InitKit` / `KitWatcher`.
3. **PhaseStepper:** five steps (`AgTabs`-like styling, no interaction).
   - Active step: a primary-colored pulse dot. Done: a check. Skipped: dashed and muted.
   - The loop badge `↺2` appears on Implement when it has looped.
   - It follows the design-system state colors, and has `aria-current="step"` on the active step.
4. **GateBanner:** a `bg-warning/15` alert with the text "Plan ready for approval", **View plan**
   (opens the Artifacts tab), **Approve** and **Reject…** (an `AgModal` with a required reason).
   All calls go through `send()` with antiforgery.
5. **Artifacts tab:** a list by phase (plus the kit snapshot summary: version, source commit,
   file list); `ArtifactViewer` renders markdown-lite as VNodes (no `v-html`, per the CSP rules).
6. **ReposView:** a table with name, base, kit state (a badge: Missing / Invalid (n) / v1.0.0 ✓ /
   Init PR open ↗) and actions **Initialize kit** (a modal with the "Pre-fill with bootstrap run"
   switch, on by default) and **Validate**. Errors expand into a list.

## Tests
- BFF (`WebApplicationFactory`): the view model shapes; approve/reject call the use cases; missing
  antiforgery → 400; `kit/init` returns 202 with the PR URL once available.
- Vitest: PhaseStepper states and the loop badge; the GateBanner reason is required for reject;
  the `repos` store handles the Missing / Invalid / Valid states.
- Playwright: open a waiting job → approve from the banner → the stepper moves to Implement; zero
  CSP violations.

## Done when
- [ ] Exit criterion: the UI shows the phase stepper, the loop count and every artifact, and can
      approve the plan gate.
- [ ] The Repos page shows kit status and can open a Kit PR.
- [ ] Accessibility: the stepper and banner are keyboard reachable and screen-reader labelled.
