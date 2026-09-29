# T4.4 — Kit domain model, layering + `ValidateKit`

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 4 | T4.3 | M | `Agentd.Domain`, `Agentd.Application`, `Agentd.Host` (CLI) |

## Goal
Model a kit in the Domain, implement the **layering rules** (built-in defaults < organization
overrides < repo `.agentd/` < work item tags), and implement **`ValidateKit`** so that invalid kits
are rejected with clear errors, both in `agentd kit validate` and before a job starts.

## Files
- `src/Agentd.Domain/Kits/Kit.cs`, `KitFile.cs`, `KitVersion.cs`, `KitSettings.cs` — create.
- `src/Agentd.Domain/Kits/KitLayering.cs` — create: merge rules.
- `src/Agentd.Application/Kits/ValidateKit.cs` — create: the use case + `KitValidationResult`.
- `src/Agentd.Application/Kits/IKitSchemaValidator.cs` — create: port (JSON Schema validation is infrastructure).
- `src/Agentd.Infrastructure.Git/Kit/JsonSchemaKitValidator.cs` — create: validates `kit.json` against `kit/schema/kit.v1.json`.
- `src/Agentd.Host/Cli/KitCommands.cs` — create: `agentd kit validate <repo> [--path <dir>]`.

## Implementation
1. `Kit` = `KitVersion` + `KitSettings` (typed `kit.json`) + `IReadOnlyDictionary<string, KitFile>`
   (path → content + sha256).
2. `KitLayering.Merge(defaults, orgOverrides?, repoKit?, workItemTags)`:
   - **files:** a higher layer **replaces** a lower layer's file at the same path;
   - **`kit.json`:** merged key by key (objects deep-merged, arrays replaced);
   - **tags:** `ai-gate:design` / `ai-gate:plan` turn gates on; `ai-kind:<k>` applies
     `skipPhases.whenTags`;
   - the result converts to `PhasePolicy` (T4.2).
3. `ValidateKit` checks, collecting **all** errors (not just the first):
   - the `kit.json` schema (via the port);
   - every phase file uses only known placeholders (the regex `\{\{([a-zA-Z:]+)\}\}` against the
     allowlist from T4.3);
   - each template has its required headings;
   - size budgets: each phase file ≤ 6 KB, `context.md` ≤ 24 KB, and the total injected context
     ≤ 40 KB (configurable);
   - `verify.build` or `verify.test` is not empty;
   - `paths.protected` / `paths.context` are valid globs;
   - `reviewers/*.md` frontmatter parses, and `workflow.review.reviewers` names reviewers that exist.
4. Errors are `{ path, line?, code, message }`, with codes such as `KIT001 unknown placeholder`.
5. CLI: `agentd kit validate agentd` reads the kit from `origin/<base>` (via T4.5). With `--path`,
   it validates a local folder, which is useful in a repo's own CI. Exit code 1 on errors, printed
   as a table.

## Tests
- Layering: repo file overrides the default; `kit.json` deep merge; a tag turns on the design gate;
  a tag skips Design.
- Validation: each rule has a failing fixture with the expected error code; a valid kit returns no
  errors; several errors are reported together.
- CLI: `--path` on a fixture folder returns exit code 0 or 1 with the expected output.

## Done when
- [ ] `agentd kit validate --path kit/v1` passes on the shipped defaults.
- [ ] Every validation rule has a unit test with its error code.
- [ ] Layering produces the correct `PhasePolicy` for the tag cases above.
