# agentd — Web Security

This covers how the agentd Web UI and API are protected in the browser: **HttpOnly cookies
only**, **antiforgery on every state-changing request**, and a **strict Content Security Policy
with no inline or eval'd script**. Host-level concerns (secrets, prompt injection, agent
sandboxing) are covered in [Architecture §5](../architect/README.md#5-security-considerations).

Related: [UI spec](../ui/README.md) · [Design System](../design-system/README.md) · [Clean Architecture + BFF](../architect/clean-architecture-bff.md)

All browser-facing protection lives in the **BFF** (`Agentd.Bff`). The SPA never holds a credential.

---

## 1. Threat model (browser side)

| Threat | Mitigation |
|---|---|
| XSS through agent output, work item text or Discord messages rendered in the UI | Vue text interpolation only (no `v-html`); strict CSP (§3); Trusted Types (§3.4) |
| Token theft via XSS | no tokens in JS-readable storage; every cookie is `HttpOnly` (§2) |
| CSRF against `/api/*` actions (cancel, retry, message, run) | antiforgery token in a header + `SameSite=Strict` cookies (§2) |
| Cross-site WebSocket hijacking of `/hubs/events` | `Origin` check on the hub + `SameSite=Strict` (§2.5) |
| Clickjacking | `frame-ancestors 'none'` (§3) |
| Malicious links to external content | `rel="noopener noreferrer"`, `Referrer-Policy: no-referrer` (§4) |
| Agents abusing the browser API | agents only reach `/mcp`, which uses per-job bearer tokens and a separate auth scheme; it never accepts cookies (§2.6) |

---

## 2. Cookies and antiforgery

### 2.1 Cookies

| Cookie | Purpose | Flags |
|---|---|---|
| `__Host-agentd.auth` | auth session (Discord OAuth mode) | `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/` |
| `__Host-agentd.af` | antiforgery **cookie token** | `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/` |

- **No cookie is readable by JavaScript.** This deliberately differs from the common SPA
  pattern of a JS-readable `XSRF-TOKEN` cookie.
- The `__Host-` prefix forces `Secure`, `Path=/` and no `Domain`. Browsers treat
  `http://localhost` as a secure context, so this works in local development too. If a browser
  rejects it, drop the prefix in the `Development` environment only.
- `SameSite=Strict` is safe here because agentd is never embedded in, or navigated to with side
  effects from, another site.
  - **Exception:** the OAuth callback. The correlation and nonce cookies that ASP.NET Core issues
    for the Discord handshake must use `SameSite=Lax`, which is the handler's default.

### 2.2 How the SPA gets the request token without a readable cookie

ASP.NET Core antiforgery uses two halves: the **cookie token** (HttpOnly, sent automatically) and
the **request token** (which the client must echo back in a header). Because the SPA cannot read
cookies, the request token is delivered in a **response body**:

```mermaid
sequenceDiagram
    participant UI as Vue SPA
    participant API as agentd (ASP.NET Core)
    UI->>API: GET /bff/antiforgery (same-origin, cookies sent)
    API-->>UI: Set-Cookie: __Host-agentd.af=<cookie token>; HttpOnly<br/>{ "token": "<request token>" }
    Note over UI: keep token in memory only<br/>(module variable, not localStorage)
    UI->>API: POST /api/jobs/42/cancel<br/>X-XSRF-TOKEN: <request token><br/>Cookie: __Host-agentd.af=...; __Host-agentd.auth=...
    API->>API: IAntiforgery.ValidateRequestAsync ✔
    API-->>UI: 204
```

Why this is safe: an attacking site can make the browser *send* cookies, but under the same-origin
policy it cannot *read* the JSON response from `/bff/antiforgery`, and CORS is not enabled.
So it can never obtain the request token.

### 2.3 Server configuration

```csharp
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-XSRF-TOKEN";
    o.Cookie.Name = "__Host-agentd.af";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.Path = "/";
    o.SuppressXFrameOptionsHeader = true;   // CSP frame-ancestors covers it (§3)
});

builder.Services.ConfigureApplicationCookie(o => { /* only in DiscordOAuth mode */
    o.Cookie.Name = "__Host-agentd.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
});

var app = builder.Build();
app.UseSecurityHeaders();        // §4
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Issue the request token; the cookie token is set as a side effect.
app.MapGet("/bff/antiforgery", (IAntiforgery af, HttpContext ctx) =>
{
    var tokens = af.GetAndStoreTokens(ctx);
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { token = tokens.RequestToken });
});

// Validate every unsafe method on both BFF groups.
var bff = app.MapGroup("/bff").AddEndpointFilter<ValidateAntiforgeryFilter>();   // login/user are GET; logout is POST
var api = app.MapGroup("/api")
             .RequireAuthorization()                    // when auth is enabled
             .AddEndpointFilter<ValidateAntiforgeryFilter>();
```

```csharp
sealed class ValidateAntiforgeryFilter(IAntiforgery af) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
            && !HttpMethods.IsOptions(http.Request.Method))
        {
            if (!await af.IsRequestValidAsync(http))
                return Results.Problem(statusCode: 400, title: "antiforgery_invalid");
        }
        return await next(ctx);
    }
}
```

Notes:

- `UseAntiforgery()` validates form-bound endpoints automatically, but agentd's JSON endpoints need
  the explicit filter above. **Every** POST, PUT, PATCH or DELETE under `/api` is covered, with no
  per-endpoint opt-in.
- GET endpoints must stay free of side effects.
- The request token is bound to the signed-in user. After **login or logout**, the SPA must fetch a
  new token.
- Even in `Auth: None` mode (localhost only), antiforgery stays on. It stops a malicious web page
  in the developer's own browser from driving `http://127.0.0.1:7780/api/...`.

### 2.4 Client (`web/src/api/http.ts`)

```ts
let xsrf: string | null = null                        // memory only

async function ensureToken(force = false) {
  if (xsrf && !force) return xsrf
  const r = await fetch('/bff/antiforgery', { credentials: 'same-origin' })
  xsrf = (await r.json()).token
  return xsrf
}

export async function send<T>(method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', url: string, body?: unknown): Promise<T> {
  for (let attempt = 0; attempt < 2; attempt++) {
    const res = await fetch(url, {
      method,
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': await ensureToken(attempt > 0) },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
    if (res.status === 400 && (await isAntiforgeryError(res)) && attempt === 0) continue   // token stale → refetch once
    if (!res.ok) throw await ApiError.from(res)
    return res.status === 204 ? (undefined as T) : res.json()
  }
  throw new ApiError(400, 'antiforgery_invalid')
}

export function resetXsrf() { xsrf = null }            // call after login/logout
```

### 2.5 SignalR hub (`/hubs/events`)

- WebSocket upgrades are not covered by antiforgery tokens. Instead:
  - **Origin allowlist:** reject negotiate and upgrade requests whose `Origin` is not the app's own
    origin (via middleware on `/hubs`).
  - `SameSite=Strict` auth cookie, so cross-site pages cannot open an authenticated connection.
- Hub methods are **read-only** (`Subscribe`, `Unsubscribe`). Every state-changing action goes
  through REST, which has antiforgery protection.

### 2.6 MCP endpoint (`/mcp`)

- It is used only by the local `claude` processes. It has its own authentication scheme (a
  per-job bearer token) and **no cookie scheme**, and it is excluded from the `/api` group and the
  antiforgery filter.
- It rejects any request that carries an `Origin` header (browsers always send one; the CLI does not).

---

## 3. Content Security Policy

### 3.1 Policy (production)

```
Content-Security-Policy:
  default-src 'none';
  script-src 'self';
  style-src 'self';
  img-src 'self' data:;
  font-src 'self';
  connect-src 'self';
  manifest-src 'self';
  base-uri 'none';
  form-action 'self';
  frame-ancestors 'none';
  object-src 'none';
  require-trusted-types-for 'script';
  trusted-types vue;
  upgrade-insecure-requests;
  report-to csp
Reporting-Endpoints: csp="/api/csp-report"
```

**No inline or eval'd script:**

- `script-src 'self'` with **no** `'unsafe-inline'`, `'unsafe-eval'`, nonces, hashes or
  third-party hosts. Every script is a same-origin file emitted by Vite.
- `default-src 'none'`: anything not listed explicitly is blocked.

### 3.2 What the UI must do to comply

| Rule | How |
|---|---|
| No inline `<script>` in `index.html` | Vite production builds emit `<script type="module" src="/assets/...">` only. The pre-paint theme loader is an external file, `public/theme-init.js`, loaded with a plain `<script src>` in `<head>`. |
| No `eval` / `new Function` | use the **runtime-only** Vue build (the Vite default: SFC templates are precompiled). Never alias `vue` to `vue/dist/vue.esm-bundler.js`, and never use in-DOM or string templates. |
| No inline event handlers (`onclick="..."`) | Vue `@click` bindings are attached with `addEventListener`, so they are fine. |
| No inline `<style>` / `style="..."` in HTML | all CSS comes from the built `/assets/*.css`. Vue `:style` bindings and base-ui-vue positioning set styles through the CSSOM (`el.style.*`), which `style-src 'self'` allows. |
| No external fonts, CDNs or analytics | system font stack (Design System §3), icons as inline SVG components |
| `connect-src 'self'` | REST and SignalR are same-origin. CSP Level 3 browsers match `ws:`/`wss:` to the same origin under `'self'`. |
| `form-action 'self'` | make **login a link** (`<a href="/bff/login">`, a GET navigation), not a form POST. A POST that redirects to `discord.com` would be blocked. Logout is a same-origin `POST /bff/logout` with the antiforgery header. |

### 3.3 Development

The Vite dev server injects inline HMR code, so the strict policy is **not** applied to the Vite dev
origin (`npm run dev`). To catch violations before merge:

- the daemon always sends the strict policy (including in `Development`), so the production build
  served by the daemon is tested under the real CSP;
- CI runs Playwright smoke tests against the built app and fails on any `securitypolicyviolation`
  event or `/api/csp-report` hit.

### 3.4 Trusted Types

`require-trusted-types-for 'script'` makes DOM XSS sinks (`innerHTML`, `outerHTML`,
`insertAdjacentHTML`, script `src` assignment) throw unless they receive a Trusted Types value.
Vue 3.5+ creates a Trusted Types policy named `vue` for its own internal use, which
`trusted-types vue` allows.

- agentd code never calls these sinks directly. Agent output is rendered as text nodes
  (`{{ }}`), and the markdown-lite renderer builds VNodes, not HTML strings.
- Rollout: ship it first as `Content-Security-Policy-Report-Only` for Trusted Types, confirm that
  base-ui-vue and the SignalR client raise no violations, then enforce it.

### 3.5 Reporting

`POST /api/csp-report` (anonymous, rate-limited, body capped at 8 KB) logs violations as structured
warnings with the blocked URI, directive and document URL. It is exempt from the antiforgery filter
because browsers send these reports without our header.

---

## 4. Other security headers

Set in one `UseSecurityHeaders()` middleware for every response:

| Header | Value |
|---|---|
| `Content-Security-Policy` | §3.1 |
| `X-Content-Type-Options` | `nosniff` |
| `Referrer-Policy` | `no-referrer` |
| `Cross-Origin-Opener-Policy` | `same-origin` |
| `Cross-Origin-Resource-Policy` | `same-origin` |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=(), payment=(), usb=()` |
| `Strict-Transport-Security` | `max-age=31536000` (only when served over TLS or behind a TLS proxy) |
| `Cache-Control` | `no-store` for `/api/*`; `public, max-age=31536000, immutable` for hashed `/assets/*`; `no-cache` for `index.html` |

CORS: **not enabled.** The UI and API are the same origin, and no cross-origin clients are supported.

---

## 5. Checklist (per PR touching web/ or the host)

- [ ] No `v-html`, `innerHTML`, `eval`, `new Function`, or string templates.
- [ ] No inline `<script>` / `<style>` / `style=""` / `on*=""` in `index.html` or public files.
- [ ] No new external origin in the CSP; any new directive is justified in this document.
- [ ] New unsafe-method endpoints live under the `/api` or `/bff` group, so the antiforgery filter applies.
- [ ] GET endpoints have no side effects.
- [ ] No new cookie without `HttpOnly`, `Secure`, `SameSite=Strict` and the `__Host-` prefix.
- [ ] Playwright CSP smoke test passes (no violations).
