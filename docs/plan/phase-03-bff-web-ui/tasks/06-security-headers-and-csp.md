# T3.6 — Security headers, strict CSP and CSP reporting

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | T3.2, Phase 0 (web build in `wwwroot`) | M | Agentd.Bff / web |

## Goal
Send a strict Content Security Policy and the other security headers on **every** response, collect
violation reports, and make sure the built Vue app runs under that policy with no inline or eval'd
script. Trusted Types start in **report-only** mode.

## Files
- `src/Agentd.Bff/Security/SecurityHeadersMiddleware.cs` — create: `UseSecurityHeaders()`.
- `src/Agentd.Bff/Security/CspPolicy.cs` — create: builds the enforced and report-only header values.
- `src/Agentd.Bff/Endpoints/CspReportEndpoint.cs` — create: `POST /api/csp-report`, outside the antiforgery groups.
- `src/Agentd.Host/Program.cs` — modify: `app.UseSecurityHeaders()` first in the pipeline.
- `src/Agentd.Web/Views/Shared/_Layout.cshtml` — verify: no inline script or style; loads `/theme-init.js` with `<script src>`.
- `src/Agentd.Web/vite.config.ts` — verify: runtime-only Vue (no `vue` alias to the full build); `build.modulePreload` doesn't inject inline code.

## Implementation
1. **Enforced policy** (a single header line; see [security §3.1](../../../security/README.md#31-policy-production)):
   ```
   default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self';
   connect-src 'self'; manifest-src 'self'; base-uri 'none'; form-action 'self';
   frame-ancestors 'none'; object-src 'none'; upgrade-insecure-requests; report-to csp
   ```
   Omit `upgrade-insecure-requests` when serving plain HTTP on loopback.
2. **Report-only header:**
   `Content-Security-Policy-Report-Only: require-trusted-types-for 'script'; trusted-types vue; report-to csp`.
   Add `Reporting-Endpoints: csp="/api/csp-report"`.
3. **Other headers:**
   - `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`;
   - `Cross-Origin-Opener-Policy: same-origin`, `Cross-Origin-Resource-Policy: same-origin`;
   - `Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=(), usb=()`;
   - `Strict-Transport-Security` **only** when the request is HTTPS (after `ForwardedHeaders`).
4. **Caching:**
   - `/api/*` and `/bff/*` → `no-store`;
   - hashed `/assets/*` → `public, max-age=31536000, immutable`;
   - the Razor shell (`/` and the SPA fallback) → `no-cache`.
5. **The CSP applies in Development too,** when served by the Host. Only the Vite dev server origin
   (Aspire `web` resource) runs without it, because HMR injects inline code.
6. **`POST /api/csp-report`:**
   - anonymous;
   - accepts `application/reports+json` and the legacy `application/csp-report`;
   - bodies over 8 KB → 413;
   - rate-limited per IP with the built-in rate limiter (e.g. 30/minute);
   - logs one structured warning per report: `directive`, `blockedURL`, `documentURL`, `disposition`.
7. **Frontend compliance:**
   - add a CI check with `grep` that fails on `v-html`, `innerHTML`, `new Function` or `eval(` in
     `web/src`;
   - check that the rendered Razor shell has no inline `<script>` without `src`, and no `style=`.

## Tests
- `Bff.Tests`:
  - every route type (`/`, `/assets/x.js`, `/api/dashboard`, a 404) carries the CSP and the other
    headers;
  - the Razor shell is `no-cache`, `/api` is `no-store`;
  - HSTS is present only over HTTPS;
  - `POST /api/csp-report` with a sample report → 204 and a log entry; a 10 KB body → 413;
    the 31st request in a minute → 429.
- CI script checks on `dist/` and `web/src` (above).
- The Playwright checks are in T3.12.

## Done when
- [x] Every response carries the enforced CSP and the security headers.
- [ ] The built UI runs with **zero** CSP violations; Trusted Types violations are reported, not enforced.
- [x] Violation reports are logged and rate-limited.
- [x] CI fails on inline script or style, or on use of dangerous DOM sinks.

## As built
- **Code:** `Security/SecurityHeaders.cs` (`UseSecurityHeaders`, the policy strings) and
  `Endpoints/CspReportEndpoint.cs`. The Host calls `UseSecurityHeaders` first. Headers are added in
  `Response.OnStarting`, so static files, the Razor shell, API responses and 404s all get them.
- **Policy details:**
  - The policy includes `report-uri /api/csp-report` next to `report-to csp`, because Firefox doesn't
    support `report-to` yet.
  - `upgrade-insecure-requests` and HSTS are sent only over https.
- **Development with the Vite dev server proxied** (`ViteHelper.UsesDevServer`):
  `style-src 'self' 'unsafe-inline'`, because Vite's HMR client injects `<style>` elements. It's
  never on otherwise, and `script-src` stays `'self'`. The production build served by the daemon
  always gets the strict policy.
- **Caching:**
  - `/api` and `/bff` → `no-store`;
  - `/_content/Agentd.Web/assets/*` (hashed) → `public, max-age=31536000, immutable`;
  - HTML (the Razor shell) → `no-cache`.
- **`/api/csp-report`:**
  - anonymous and outside the antiforgery groups;
  - reads at most 8 KB + 1 byte (413 beyond 8 KB);
  - accepts the Reporting API array and the legacy `csp-report` object;
  - logs one warning per violation;
  - fixed-window rate limit of 30/minute per IP (429), through `UseRateLimiter`;
  - left out of the OpenAPI document.
- **Data Protection keys are persisted** (added): `~/.agentd/keys` (0700), application name
  `agentd`. The antiforgery cookie (and the SSO cookies in Phase 5) survive restarts.
- **CI:** the web job fails on `v-html`, `innerHTML`, `outerHTML`, `insertAdjacentHTML`,
  `document.write`, `new Function` or `eval(` under `ClientApps/`. The shell's no-inline check
  already lives in `Agentd.Web.Tests`.
- **"Zero CSP violations in the built UI"** is checked in a real browser by T3.12 (Playwright). The
  shell, `theme-init.js` and the runtime-only Vue build were reviewed here.
- **Tests:**
  - `Agentd.Web.Tests/SecurityHeaderTests` runs the daemon's real pipeline: `/`, a client route, a
    hashed asset, a static file, `/bff/user`, a `/api` 404, https (HSTS + upgrade), and a report
    without a token → 204.
  - `Bff.Tests/CspReportTests`: both report formats logged, 413, 429 on the 31st request, and junk
    input ignored.
