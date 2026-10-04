# T3.14: Cloudflare Access sign-in mode

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | T3.5 (antiforgery, forwarded-request guard) | S | Agentd.Bff / Agentd.Host |

## Goal
Publish the Web UI on the internet through **Cloudflare Tunnel**, with **Cloudflare Access**
handling sign-in, before Phase 5 SSO exists. Cloudflare signs in the user at its edge; agentd trusts
a request only when it carries a valid Access token, and never trusts it because it came from
localhost.

`cloudflared` runs on the daemon's server and connects to `127.0.0.1`, so every tunnelled request
looks like a loopback request. Without this task, the T3.5 guard returns 401 for them, which is the
safe default.

## Files
- `src/Agentd.Bff/Http/CloudflareAccessAuthentication.cs`: create. An authentication handler for
  scheme `CloudflareAccess`.
- `src/Agentd.Bff/BffModule.cs`: modify. Choose the scheme from `Auth.Mode`.
- `src/Agentd.Host/appsettings.json`: modify. `Auth.CloudflareAccess` section (no secrets in it).
- `docs/architect/deployment.md`, `docs/security/authentication.md`: modify. As built.

## Implementation
1. **Config:**
   ```jsonc
   "Auth": {
     "Mode": "CloudflareAccess",                        // None | CloudflareAccess | Sso (Phase 5)
     "CloudflareAccess": {
       "TeamDomain": "<team>.cloudflareaccess.com",
       "Audience": "<the Access application's AUD tag>",
       "AllowedEmails": ["you@example.com"],           // optional extra filter on top of the Access policy
       "AdminEmails": ["you@example.com"]
     }
   }
   ```
   The values aren't secret: the AUD tag only identifies the application.
2. **Token check:**
   - Read the `Cf-Access-Jwt-Assertion` header. Fall back to the `CF_Authorization` cookie only for
     the SignalR WebSocket upgrade.
   - Validate the JWT with `Microsoft.IdentityModel.JsonWebTokens`:
     - signing keys from `https://<TeamDomain>/cdn-cgi/access/certs`, cached and refreshed on an
       unknown `kid`;
     - issuer `https://<TeamDomain>`, audience = `Audience`, lifetime with 1 minute of clock skew.
   - On success: a principal with `name` = the `email` claim, and the `Admin` role when the email is
     in `AdminEmails`. Otherwise `User`, until Phase 5 roles exist.
   - A missing or invalid token → 401. Loopback gives no exemption in this mode.
3. **Startup checks:** `Mode = CloudflareAccess` requires `TeamDomain` and `Audience`. With this mode
   the daemon may still bind loopback only. `cloudflared` is the only way in.
4. **https behind the tunnel:** `cloudflared` connects over plain http. Enable `UseForwardedHeaders`
   for `X-Forwarded-Proto` / `X-Forwarded-Host`, trusting only the loopback proxy, so the daemon
   sees `https`. Then the antiforgery cookie is `__Host-agentd.af` + `Secure` (T3.5 As built), and
   the hub origin check compares against the public host.
5. **Antiforgery (T3.5) stays on.** Access cookies are sent cross-site like any other cookie, so CSRF
   protection is still needed.
6. **`/mcp` is never published.** The deployment doc's tunnel config routes only `/`, `/api`, `/bff`,
   `/hubs` and `/_content`. In addition, the MCP endpoint rejects any request that carries
   `Cf-Connecting-IP`.
7. **Deployment doc:**
   - `cloudflared tunnel create agentd`;
   - an ingress rule `agentd.<domain>` → `http://127.0.0.1:<port>`, plus the `/mcp` exclusion;
   - an Access self-hosted application with an allow policy for your emails;
   - copy the AUD tag into config.

## Tests
- `Bff.Tests`, with an in-test RSA key standing in for Cloudflare's certs endpoint:
  - a valid token → 200 as that email;
  - wrong audience, wrong issuer, expired, or a bad signature → 401;
  - no header from loopback → 401 in this mode;
  - an email in `AdminEmails` gets `Admin`;
  - an unknown `kid` triggers one key refresh;
  - `/mcp` with `Cf-Connecting-IP` → 403.

## Done when
- [ ] With Cloudflare Tunnel and Access, the UI works from another network after signing in.
- [ ] Without a valid Access token, every `/api`, `/bff` and hub request is 401, including from
      localhost.
- [ ] `/mcp` can't be reached through the tunnel.
- [ ] The deployment doc has the full tunnel and Access setup.
