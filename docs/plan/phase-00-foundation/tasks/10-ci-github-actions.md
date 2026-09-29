# T0.10 — CI with GitHub Actions

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 0 | T0.1–T0.9 | S | .github/workflows |

> **Moved after Phase 0:** `web/` is now **`src/Agentd.Web`**, a Razor class library whose
> `ClientApps/<app>/` folders hold the Vue SPAs and whose `package.json` sits at the project root. It
> also has `_Layout.cshtml` + `ViteHelper`, with assets under `/_content/Agentd.Web/`. See
> [Architecture §3.9 → Serving](../../../architect/README.md) and [UI §10](../../../ui/README.md#10-folder-structure).

## Goal
Every PR and every push to `main` is built, formatted, type-checked, linted and tested, for both
.NET and web. Merging a red build is blocked.

## Files
- `.github/workflows/ci.yml`: create.
- `.github/dependabot.yml`: create. Weekly `nuget`, `npm` and `github-actions` updates, grouped.

## Implementation
```yaml
name: ci
on:
  pull_request:
  push:
    branches: [main]
permissions:
  contents: read
concurrency:
  group: ci-${{ github.ref }}
  cancel-in-progress: true

jobs:
  dotnet:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet restore Agentd.slnx
      - run: dotnet format Agentd.slnx --verify-no-changes --no-restore
      - run: dotnet build Agentd.slnx --no-restore -c Release
      # MSTest on Microsoft.Testing.Platform: dotnet test runs in MTP mode (global.json "test.runner").
      - run: >
          dotnet test --solution Agentd.slnx --no-build -c Release
          --filter "TestCategory!=Aspire"
          --report-trx --results-directory TestResults
          --coverage --coverage-output-format cobertura
        # Testcontainers uses the runner's Docker; Aspire-category tests are filtered out here.
        env: { DOTNET_CLI_TELEMETRY_OPTOUT: "1" }
      - uses: actions/upload-artifact@v4
        if: always()
        with: { name: dotnet-test-results, path: TestResults }

  web:
    runs-on: ubuntu-latest
    defaults: { run: { working-directory: web } }
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with: { node-version-file: .nvmrc, cache: npm, cache-dependency-path: web/package-lock.json }
      - run: npm ci
      - run: npx vue-tsc --noEmit
      - run: npm run lint
      - run: npm run test -- --run
      - run: npm run build
      - name: No inline script/style in built index.html
        run: "! grep -Eq '<script>|<style' ../src/Agentd.Host/wwwroot/index.html"
```
- Pin the action versions to the current majors at implementation time. Consider pinning to SHAs
  in Phase 10.
- Exclude Aspire-hosting tests with `--filter "TestCategory!=Aspire"` (MSTest filter syntax).
- The MTP options (`--solution`, `--report-trx`, `--coverage`) come from .NET 10's MTP-mode
  `dotnet test` and the MSTest.Sdk default extensions. Verify the exact option names against the
  pinned SDK and MSTest versions.
- Optional: publish the TRX files as a check summary, and upload the Cobertura coverage report as an artifact.
- **Branch protection on `main`** (a manual GitHub setting): require the `ci / dotnet` and `ci / web`
  checks, and require PRs.

## Tests
- The workflow is the test. Open the Phase 0 PR and confirm both jobs run and pass. Push a commit
  with a format violation and confirm the `dotnet` job fails.

## Done when
- [ ] Both jobs are green on the Phase 0 PR.
- [ ] Branch protection requires them.
- [ ] Dependabot is configured.
