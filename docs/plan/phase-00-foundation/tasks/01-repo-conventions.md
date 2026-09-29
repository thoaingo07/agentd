# T0.1 — Repository conventions & build settings

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 0 | — | S | repo root |

## Goal
Pin the toolchains, turn on strict compiler settings for every project, and set up central package
versioning. From here on, every project inherits the same rules.

## Files
- `global.json`: create. Pins the .NET 10 SDK.
- `Directory.Build.props`: create. Shared MSBuild properties.
- `Directory.Packages.props`: create. Central package versions.
- `.editorconfig`: create. C#, TS and Vue formatting and analyzer severities.
- `.nvmrc`: create. Contains `24`.
- `.gitignore`: modify. .NET, Node, Aspire and IDE outputs; `src/Agentd.Host/wwwroot/` (build output; Vite's `emptyOutDir` would delete a `.gitkeep` anyway).
- `.gitattributes`: create. `* text=auto eol=lf`.

## Implementation
1. `global.json`: pin the SDK band that is installed and used in CI (currently `10.0.301`), with
   `"rollForward": "latestFeature"` so CI and developers can use newer feature bands.
   ```json
   {
     "sdk": { "version": "10.0.301", "rollForward": "latestFeature" },
     "test": { "runner": "Microsoft.Testing.Platform" },
     "msbuild-sdks": { "MSTest.Sdk": "<latest stable MSTest version>" }
   }
   ```
   - `test.runner` puts `dotnet test` into **Microsoft.Testing.Platform (MTP) mode** on .NET 10
     (verify the exact setting against the installed SDK).
   - `msbuild-sdks` pins **one MSTest.Sdk version for every test project**, so test projects
     declare `<Project Sdk="MSTest.Sdk">` with no version.
2. `Directory.Build.props`:
   ```xml
   <Project>
     <PropertyGroup>
       <TargetFramework>net10.0</TargetFramework>
       <Nullable>enable</Nullable>
       <ImplicitUsings>enable</ImplicitUsings>
       <LangVersion>latest</LangVersion>
       <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
       <AnalysisLevel>latest-recommended</AnalysisLevel>
       <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
       <GenerateDocumentationFile>false</GenerateDocumentationFile>
       <InvariantGlobalization>true</InvariantGlobalization>
       <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
     </PropertyGroup>
   </Project>
   ```
3. `Directory.Packages.props`: an empty `<ItemGroup>` to start. Each later task adds its
   `PackageVersion` entries here. Aspire, Npgsql, Dapper and the test packages are pinned to the
   **latest stable versions at implementation time**. Record the chosen versions in the PR
   description.
4. `.editorconfig`: the standard .NET conventions (file-scoped namespaces, `var` where the type is
   apparent, `_camelCase` private fields), `IDE0005` (unnecessary usings) as a warning,
   `dotnet_diagnostic.CA1848.severity = suggestion`, plus 2-space indentation for
   `*.{ts,vue,json,css}`.
5. `.gitignore`: `bin/`, `obj/`, `*.user`, `.vs/`, `.idea/`, `node_modules/`, `web/dist/`,
   `src/Agentd.Host/wwwroot/*` with `!src/Agentd.Host/wwwroot/.gitkeep`, `*.local.json`,
   `TestResults/`, `playwright-report/`.

## Tests
- None of its own. T0.2 is the first build that exercises these settings.

## Done when
- [ ] `dotnet --version` inside the repo resolves to a 10.0 SDK.
- [ ] `nvm use` picks Node 24.
- [ ] A deliberately unused variable in any project fails the build, because warnings are errors.
