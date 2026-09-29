# T0.8 — Host serves the SPA

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 0 | T0.5, T0.7 | S | Agentd.Host, Bff.Tests |

## Goal
The Host serves the built Vue app from `wwwroot`, with client-side routes falling back to
`index.html`, so production is one deployable. It must not interfere with `/api`, `/bff`, `/hubs`,
`/mcp` or the health endpoints.

## Files
- `src/Agentd.Host/Program.cs`: modify. Static files + fallback.
- `src/Agentd.Host/wwwroot/.gitkeep`: create.
- `src/Agentd.Host/Agentd.Host.csproj`: modify. An optional `BuildWeb` MSBuild target.
- `tests/Agentd.Bff.Tests/SpaHostingTests.cs`: create.

## Implementation
1. Pipeline order:
   ```csharp
   app.UseDefaultFiles();
   app.UseStaticFiles();                  // cache headers are tuned in Phase 3 (security headers)
   app.MapDefaultEndpoints();             // /healthz, /alive
   // Phase 1+: app.MapMcp("/mcp"); Phase 3+: /api, /bff, /hubs
   app.MapFallbackToFile("index.html");   // must be last
   ```
   Use `UseStaticFiles` in Phase 0. Switching to `MapStaticAssets()` (build-time fingerprinting
   and compression) happens in Phase 3, together with the MSBuild integration below, because
   `MapStaticAssets` needs the web output to exist when `dotnet build` runs.
2. **The fallback must not swallow API routes.** Unknown `/api/*`, `/bff/*`, `/hubs/*` and
   `/mcp*` paths return 404, not `index.html`. Enforce this with a short-circuit endpoint mapped
   before the fallback, or a fallback route constraint.
3. `BuildWeb` target: runs when `-p:BuildWeb=true`, which CI and publish set. It runs `npm ci && npm
   run build` in `../../web` before the build. Local `dotnet build` skips it; developers use Aspire
   and Vite instead.
   ```xml
   <Target Name="BuildWeb" BeforeTargets="BeforeBuild" Condition="'$(BuildWeb)' == 'true'">
     <Exec WorkingDirectory="$(MSBuildProjectDirectory)/../../web" Command="npm ci &amp;&amp; npm run build" />
   </Target>
   ```

## Tests
- `SpaHostingTests` (WebApplicationFactory, with a temporary `wwwroot` holding a stub `index.html`):
  - `GET /` → 200 HTML;
  - `GET /jobs/42` (a client route) → 200 HTML;
  - `GET /api/nope` → 404, not HTML;
  - `GET /healthz` → 200.

## Done when
- [ ] `dotnet run --project src/Agentd.Host -p:BuildWeb=true` serves the built app at `/`, and deep
      links work after a refresh.
- [ ] API-like unknown paths return 404.
- [ ] The tests pass.
