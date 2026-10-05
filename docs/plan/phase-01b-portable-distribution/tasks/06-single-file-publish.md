# T1b.6 — Self-contained single-file publish

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1b | — | M | Host, build |

## Goal
Publish `agentd` as one self-contained executable per platform, with the web assets and the migrations inside,
so a server needs nothing but the binary (and PostgreSQL).

## Files
- `src/Agentd.Host/Agentd.Host.csproj`: modify. Publish profile properties: `SelfContained`, `PublishSingleFile`,
  `IncludeNativeLibrariesForSelfExtract`, invariant globalization off (cultures needed), trimming **off**.
- `src/Agentd.Web/Agentd.Web.csproj`: modify. The Vite build runs before publish, and `wwwroot` is embedded or published
  next to the binary as static web assets.
- `src/Agentd.Host/DaemonHost.cs`: modify. In a published build, serve the web assets from the bundle (not the
  development static-web-assets manifest).
- `build/publish.sh`: create. `dotnet publish -r <rid>` for linux-x64, linux-arm64, osx-arm64, win-x64.

## Implementation
1. Migrations (`.sql` files) are embedded resources in the Migrator assembly, so `agentd db migrate` works from the
   single file.
2. The content root is the binary's directory; appsettings come from embedded defaults, config from `~/.agentd`.
3. A smoke test in CI: publish linux-x64, run `agentd --version`, `agentd db migrate` against a service container,
   start `daemon run`, and fetch `/` and an asset.

## Tests
- The published binary serves the dashboard and its assets with no `wwwroot` next to it.
- `db migrate` from the published binary applies every migration.

## Done when
- [ ] One file per platform; the CI smoke test passes on linux-x64.
