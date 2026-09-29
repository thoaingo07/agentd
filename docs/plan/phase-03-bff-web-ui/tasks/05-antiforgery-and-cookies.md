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

## Tests
- `Bff.Tests`:
  - `POST /api/jobs/1/cancel` without a header → 400 `antiforgery_invalid`;
  - with a header from `/bff/antiforgery` and the cookie → 204 or 409, never 400;
  - a token issued for session A doesn't work with session B's cookie;
  - the `Set-Cookie` for `__Host-agentd.af` has `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`
    and no `Domain`;
  - no response carries `Access-Control-Allow-Origin`;
  - a GET endpoint works without the header.
- Startup test: Mode None + `Urls=http://0.0.0.0:7780` → the host fails to start.

## Done when
- [ ] Every unsafe method under `/api` and `/bff` requires a valid `X-XSRF-TOKEN`.
- [ ] No cookie set by agentd is readable by JavaScript.
- [ ] Mode None refuses non-loopback binding and non-loopback clients.
- [ ] CORS is not enabled anywhere.
