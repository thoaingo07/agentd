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
- [x] One file per platform; the CI smoke test passes on linux-x64.

## As built (2026-10-05)
- **`build/publish.sh [rid…]`** builds the web, then runs `dotnet publish -r <rid> --self-contained` with
  `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`, `EnableCompressionInSingleFile` and `DebugType=embedded`.
  It keeps **only the executable** in `artifacts/<rid>/`. `VERSION=x.y.z` sets what `agentd --version` prints.
  linux-x64 is about 55 MB.
- **Web assets:** `Agentd.Web` embeds `wwwroot/**` (logical names `wwwroot/…`, added in a target so a fresh Vite build
  is picked up). `EmbeddedWebAssets` serves them under `/_content/Agentd.Web/`, as a **fallback after the files on
  disk**, so development and `dotnet run` are unchanged. The Vite manifest is read through the same provider.
- **Defaults:** the Host embeds its `appsettings.json` (`agentd.appsettings.json`), added just above the config home
  defaults. A file on disk still overrides it, and `appsettings*.json` aren't published.
- **Not published next to the binary:** XML docs, and the web project's `package*.json` / `tsconfig.json`.
- **Globalization:** already invariant (`Directory.Build.props`), so no ICU is needed on the server.
- **Migrations:** already embedded in the Migrator assembly; `agentd db migrate` from the lone binary applies all of
  them.
- **CI:** the `e2e` job now publishes linux-x64, copies only `agentd` into an empty folder, runs `agentd db migrate`
  and `agentd daemon run` from there, and runs the Playwright suite against it. That's the smoke test, and more.
- **Trimming stays off** (reflection: MVC, FluentMigrator, System.Text.Json). Revisit only if size matters.

