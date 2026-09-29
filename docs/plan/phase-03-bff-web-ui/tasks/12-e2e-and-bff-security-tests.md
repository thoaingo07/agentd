# T3.12 — Playwright smoke tests and BFF security test suite

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | T3.4, T3.5, T3.6, T3.9, T3.10, T3.11 | M | tests / web / CI |

## Goal
Prove the Phase 3 exit criteria automatically in CI:
- the built app runs with **zero CSP violations**;
- unsafe requests without the antiforgery header fail;
- no cookie is JS-readable;
- the live stream survives a reconnect without gaps or duplicates.

## Files
- `src/Agentd.Web/e2e/playwright.config.ts` — create: base URL from `E2E_BASE_URL`; Chromium + Firefox.
- `src/Agentd.Web/e2e/fixtures.ts` — create: a CSP-violation collector, and seeding helpers (via a test-only API).
- `src/Agentd.Web/e2e/csp.spec.ts`, `antiforgery.spec.ts`, `cookies.spec.ts`, `live-stream.spec.ts` — create.
- `src/Agentd.Bff/Testing/TestSeedEndpoints.cs` — create: `/__test/*` endpoints, **mapped only when `Environment == "E2E"`**.
- `tests/Agentd.Bff.Tests/Security/*.cs` — modify/create: consolidate the T3.4–T3.6 header, antiforgery and origin tests.
- `.github/workflows/ci.yml` — modify: an `e2e` job.
- `src/Agentd.Web/package.json` — modify: `@playwright/test` devDependency and a `test:e2e` script.

## Implementation
1. **E2E host:**
   - CI builds the web (`npm ci && npm run build` → `wwwroot`) and runs `Agentd.Host` with
     `ASPNETCORE_ENVIRONMENT=E2E`, `Auth.Mode=None` bound to `127.0.0.1`, and a PostgreSQL service
     container;
   - the Claude runner is replaced by a **fake runner** that emits scripted stream-json, so no model
     calls are made;
   - test seeding endpoints create jobs and push events.
2. **CSP collector:**
   - `page.addInitScript` registers `document.addEventListener('securitypolicyviolation', …)` and
     pushes violations to `window.__cspViolations`;
   - the test also asserts that the server log (or a `/__test/csp-reports` endpoint) received **no**
     enforced-policy reports;
   - report-only Trusted Types reports are counted and printed, but don't fail the build in this
     phase.
3. **`csp.spec.ts`:** visit `/`, `/jobs/{seeded}` (all three tabs, expand a tool card, open a
   tooltip, open a modal), `/history` and `/settings`, then assert `__cspViolations` is empty and the
   response has the `content-security-policy` header.
4. **`antiforgery.spec.ts`:**
   - `request.post('/api/jobs/1/cancel')` without the header → 400 `antiforgery_invalid`;
   - through the UI, clicking Cancel → the request carries `x-xsrf-token` and succeeds.
5. **`cookies.spec.ts`:** `await page.evaluate(() => document.cookie)` is `''`, and every agentd
   cookie from `context.cookies()` has `httpOnly: true` and `sameSite: 'Strict'`.
6. **`live-stream.spec.ts`:**
   1. open the session view;
   2. seed 30 events → wait until 30 are rendered;
   3. `context.setOffline(true)` → the indicator shows Reconnecting or Offline;
   4. seed 20 more events → `setOffline(false)`;
   5. assert exactly 50 events are rendered, in `seq` order, with no duplicate `data-seq` attributes.
7. **CI `e2e` job:**
   - runs after build;
   - installs Playwright browsers with a cache;
   - uploads the trace and screenshots as artifacts on failure;
   - runs on PRs that touch `src/Agentd.Bff/**`, `src/Agentd.Host/**` or `web/**`, and always on
     `main`.
8. **Accessibility:** run `@axe-core/playwright` on each page with no serious or critical
   violations. Lighthouse ≥ 95 is checked manually per release, not in CI.

## Tests
This task *is* the tests. Also keep the `Bff.Tests` security suite green:
- headers on every route type;
- the hub Origin check;
- the loopback guard;
- no CORS headers.

## Done when
- [ ] CI runs the Playwright suite against the built app with a fake runner.
- [ ] Zero enforced CSP violations on every page and interaction covered.
- [ ] The antiforgery, cookie and reconnect tests pass in Chromium and Firefox.
- [ ] `/__test/*` endpoints cannot be mapped outside the `E2E` environment (a unit test).
