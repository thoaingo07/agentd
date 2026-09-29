# T4.3 — Kit v1 content + `kit.v1.json` schema

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 4 | — | M | `kit/` (repo root), embedded in `Agentd.Infrastructure.Git` |

## Goal
Write the **default ai-sdlc kit** that agentd ships to every repo, plus the JSON schema for
`kit.json`. This is prompt engineering as much as code. The files must be good defaults that a team
can read, understand and edit ([ai-sdlc-kit.md §1](../../../architect/ai-sdlc-kit.md#1-kit-layout-in-the-target-repo)).

## Files
- `kit/v1/kit.json` — create: defaults (see below).
- `kit/v1/README.md` — create: what the kit is and how to customize it (for humans).
- `kit/v1/context.md` — create: a skeleton with headings (Architecture, Glossary, Build & run, Where things live).
- `kit/v1/phases/{design,plan,implement,test,review}.md` — create: phase instructions with placeholders.
- `kit/v1/templates/{design,plan,test-report,review}.md` — create: required artifact structure.
- `kit/v1/reviewers/general.md` — create: the general reviewer (frontmatter + focus / ignore / output).
- `kit/v1/hooks/setup.sh` — create: a commented no-op example.
- `kit/schema/kit.v1.json` — create: the JSON Schema (draft 2020-12).
- `src/Agentd.Infrastructure.Git/Agentd.Infrastructure.Git.csproj` — modify: `<EmbeddedResource Include="../../kit/**" LinkBase="Kit" />`.
- `src/Agentd.Infrastructure.Git/Kit/EmbeddedKitSource.cs` — create: enumerate and read the embedded files by version.

## Implementation
1. `kit.json` defaults:
   ```jsonc
   {
     "$schema": "https://agentd.local/schemas/kit.v1.json",
     "kitVersion": "1.0.0",
     "workflow": { "gates": { "design": false, "plan": true }, "maxFixLoops": 3, "maxReviewLoops": 2,
                   "review": { "reviewers": ["general"] }, "skipPhases": { "whenTags": {} } },
     "verify": { "setup": [], "build": [], "test": [], "lint": [] },
     "messaging": { "providers": [] },
     "paths": { "protected": [], "context": [] },
     "baseline": {}
   }
   ```
2. Placeholders allowed in phase files (a closed set, which T4.4 validates):
   `{{workItem}}`, `{{acceptanceCriteria}}`, `{{branch}}`, `{{previousArtifacts}}`,
   `{{template}}`, `{{verifyCommands}}`, `{{learnings:<phase>}}` (renders empty until Phase 9).
3. Phase files: goal, steps, "what a good artifact looks like", and when to call `ask_developer`.
   Each ends with "Call `complete_phase` with the artifact following `{{template}}`". Keep each one
   ≤ 1.5k tokens.
4. Templates: required `##` headings, which T4.4 checks. For example `plan.md` must have `## Steps`,
   `## Acceptance criteria mapping` and `## Tests to add or change`.
5. `reviewers/general.md`: frontmatter `name`, `title`, `description`, `appliesTo: ["**"]`,
   `severityThreshold: medium`, `maxFindings: 15`.
6. Schema: every key typed, `additionalProperties: false` at the top level, and enums for gate
   names. `baseline` maps path → a `sha256:` string.
7. `EmbeddedKitSource.GetFiles("v1")` returns `(relativePath, bytes)` in a stable order.

## Tests
- The schema validates `kit/v1/kit.json`, and rejects unknown top-level keys and wrong types.
- Every phase file uses only allowed placeholders and ends with the `complete_phase` instruction.
- Every template contains its required headings.
- `EmbeddedKitSource` returns all expected files, and their bytes equal the files on disk.

## Done when
- [ ] All kit files exist, and a teammate has read them for clarity (a review in the PR).
- [ ] The schema is published at `kit/schema/kit.v1.json` and used by T4.4.
- [ ] The kit is embedded in the build output, and the tests are green.
