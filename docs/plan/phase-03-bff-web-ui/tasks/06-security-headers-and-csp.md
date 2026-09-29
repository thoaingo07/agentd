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
- [ ] Every response carries the enforced CSP and the security headers.
- [ ] The built UI runs with **zero** CSP violations; Trusted Types violations are reported, not enforced.
- [ ] Violation reports are logged and rate-limited.
- [ ] CI fails on inline script or style, or on use of dangerous DOM sinks.
