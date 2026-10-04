# T3.5 — Antiforgery with HttpOnly cookies

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | T3.2 | M | Agentd.Bff |

## Goal
Protect every state-changing request against CSRF **without any JS-readable cookie**. The cookie
half of the antiforgery token is HttpOnly. The request half is delivered in the body of
`GET /bff/antiforgery`, kept in memory by the SPA, and echoed back as `X-XSRF-TOKEN`. One endpoint
filter on the `/api` and `/bff` groups validates every unsafe method.

## Files
- `src/Agentd.Bff/Security/AntiforgerySetup.cs` — create: `AddAgentdAntiforgery()`.
- `src/Agentd.Bff/Security/ValidateAntiforgeryFilter.cs` — create.
- `src/Agentd.Bff/Endpoints/SessionEndpoints.cs` — create: `GET /bff/antiforgery` and `GET /bff/user`.
- `src/Agentd.Bff/Security/LocalUserAuthenticationHandler.cs` — create: the Mode None pseudo-user (see T3.2).
- `src/Agentd.Bff/BffModule.cs` — modify: groups get the filter; `app.UseAntiforgery()`.

## Implementation
1. **Configuration** (see [security §2.3](../../../security/README.md#23-server-configuration)):
   ```csharp
   services.AddAntiforgery(o =>
   {
       o.HeaderName = "X-XSRF-TOKEN";
       o.Cookie.Name = "__Host-agentd.af";
       o.Cookie.HttpOnly = true;
       o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
       o.Cookie.SameSite = SameSiteMode.Strict;
       o.Cookie.Path = "/";
       o.SuppressXFrameOptionsHeader = true;   // CSP frame-ancestors (T3.6)
   });
   ```
   Browsers treat `http://localhost` as a secure context, so `__Host-` works there. Confirm this on
   `127.0.0.1` in Chromium and Firefox. If either rejects it, drop the prefix only when
   `Environment == Development`.
2. **`GET /bff/antiforgery`:** `var t = af.GetAndStoreTokens(ctx); return { token = t.RequestToken }`
   with `Cache-Control: no-store`. It is authenticated, because the token is bound to the user.
3. **`GET /bff/user`:** `{ name, roles, provider }` for the current principal. In Mode None that is
   `{ name: "local", roles: ["Admin"], provider: "local" }`.
4. **The filter:**
   - for POST, PUT, PATCH and DELETE, it calls `await af.IsRequestValidAsync(http)`;
   - if the token is invalid, it returns `400` ProblemDetails with `code = "antiforgery_invalid"`
     (the SPA refetches once on this code; T3.7);
   - it is applied to `app.MapGroup("/api")` and `app.MapGroup("/bff")`;
   - `/api/csp-report` (T3.6) is mapped **outside** these groups.
5. **Mode None stays protected:** antiforgery is on, so a malicious page in the developer's browser
   can't drive `http://127.0.0.1:7780/api/...`.
6. **No CORS:** `AddCors` is not called anywhere, and a test asserts that no
   `Access-Control-Allow-Origin` header is ever sent.
7. **Loopback guard (Mode None):** at startup, if `Auth.Mode == None` and any bound address is not
   loopback, **refuse to start** with a clear error. `LocalUserAuthenticationHandler` also rejects
   requests whose `RemoteIpAddress` is not loopback, as a second layer.
   - **Forwarded requests are not local** (added 2026-10-03). A reverse proxy or tunnel on the same
     server (`cloudflared`, nginx, Caddy) connects from `127.0.0.1`, which would make every visitor
     the local Admin. So a request carrying `Forwarded`, `X-Forwarded-For`, `X-Real-IP`,
     `Cf-Connecting-IP` or `Cf-Ray` is rejected (401) in Mode None. Publishing through Cloudflare
     uses Mode `CloudflareAccess` (T3.14) instead.

## Tests
- `Bff.Tests`:
  - `POST /api/jobs/1/cancel` without a header → 400 `antiforgery_invalid`;
  - with a header from `/bff/antiforgery` and the cookie → 204 or 409, never 400;
  - a token issued for session A doesn't work with session B's cookie;
  - the `Set-Cookie` for `__Host-agentd.af` has `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`
    and no `Domain`;
  - no response carries `Access-Control-Allow-Origin`;
  - a GET endpoint works without the header.
- A loopback request with `X-Forwarded-For` or `Cf-Connecting-IP` → 401 in Mode None.
- Startup test: Mode None + `Urls=http://0.0.0.0:7780` → the host fails to start.

## Done when
- [x] Every unsafe method under `/api` and `/bff` requires a valid `X-XSRF-TOKEN`.
- [x] No cookie set by agentd is readable by JavaScript.
- [x] Mode None refuses non-loopback binding, non-loopback clients, and forwarded requests.
- [x] CORS is not enabled anywhere.

## As built
- **Cookie name depends on the scheme.** ASP.NET Core refuses to issue an antiforgery cookie with
  `SecurePolicy = Always` on a plain-http request. It throws, so the `__Host-` cookie can't work on
  `http://127.0.0.1:7780`, whatever the browser thinks of loopback.
  - When every configured URL is https, the cookie is `__Host-agentd.af` (`Secure`).
  - Otherwise it's `agentd.af` (`SameAsRequest`). That's only allowed on loopback, because Mode None
    refuses anything else.
  - Both are `HttpOnly`, `SameSite=Strict`, `Path=/`, with no `Domain`.
  - T3.14 (Cloudflare) terminates TLS at the edge. It needs forwarded-proto handling, so the daemon
    sees https and uses `__Host-`.
- **Mode None:**
  - `Agentd:Auth:Mode`: `None`, `CloudflareAccess` (T3.14) or `Sso` (Phase 5). Modes that aren't
    built yet fail startup.
  - `BffAuthOptionsValidator` (`ValidateOnStart`) fails startup if any `urls` /
    `Kestrel:Endpoints` URL isn't loopback, or if `http_ports` / `https_ports` (every interface)
    are set.
  - A URL that isn't a URL at all is left to Kestrel, which refuses to bind it anyway.
- **Forwarded requests:** `LocalUserAuthenticationHandler` treats any forwarding header
  (`Forwarded`, `X-Forwarded-For`, `X-Forwarded-Host`, `X-Real-IP`, `Cf-Connecting-IP`, `Cf-Ray`) as
  not local → 401.
- **Code layout:**
  - `Security/AntiforgeryFilter.cs` is an endpoint filter on the `/api` and `/bff` groups.
    `app.UseAntiforgery()` isn't needed: that middleware only covers form-bound endpoints, and the
    filter calls `IAntiforgery.IsRequestValidAsync` itself.
  - The handler stays in `Http/LocalUserAuthentication.cs` (from T3.2) and isn't moved to
    `Security/`.
- **Endpoints:** `GET /bff/antiforgery` (`{ token }`, `no-store`) and `GET /bff/user`
  (`{ name, roles, provider }`).
- **Tests (`BffSecurityTests`):**
  - header required (400 `antiforgery_invalid`);
  - a token only works with its own cookie;
  - cookie flags over http and https;
  - `/bff/user`;
  - forwarded headers → 401;
  - no CORS headers;
  - the loopback checks (including `*`, `+`, `0.0.0.0`, `http_ports`, Kestrel endpoints);
  - the host failing to start.

  The action endpoint tests now go through `AntiforgeryClient`, which does what the SPA does.
