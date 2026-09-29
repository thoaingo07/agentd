# T4.6 — `InitKit` + bootstrap run + Kit PR

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 4 | T4.3, T4.5, Phase 1 (PR publishing, Claude runner) | L | `Agentd.Application`, `Agentd.Infrastructure.Git`, `Agentd.Host` (CLI) |

## Goal
Scaffold the default kit into a repo through a **Kit PR**, optionally pre-filled by a short
**read-only bootstrap run** that detects the stack, the build/test/lint commands and the
conventions. The team then reviews and edits the PR ([ai-sdlc-kit.md §2.1](../../../architect/ai-sdlc-kit.md#21-init)).

## Files
- `src/Agentd.Application/Kits/InitKit.cs` — create: the use case (`InitKit(repo, bootstrap: bool, requestedBy)`).
- `src/Agentd.Application/Kits/KitPrefill.cs` — create: `{ ContextMarkdown, Verify, ReviewerConventions }`.
- `src/Agentd.Infrastructure.Git/Kit/KitWriter.cs` — create: writes the files + `baseline` hashes into a worktree.
- `src/Agentd.Infrastructure.Claude/BootstrapRunner.cs` — create: a read-only Claude run that returns `KitPrefill` JSON.
- `src/Agentd.Host/Cli/KitCommands.cs` — modify: `agentd kit init <repo> [--no-bootstrap]`.
- `kit/v1/bootstrap/prompt.md` — create: the bootstrap instructions + JSON output schema.

## Implementation
1. **Guard:** if `origin/<base>` already has `.agentd/` → `Result.Fail(KitExists)` ("use
   `kit upgrade`" in Phase 10). If an `agentd/kit-init` PR is already open → return its URL.
2. **Branch:** a temporary worktree on the new branch `agentd/kit-init` from `origin/<base>`.
3. **Write defaults:** copy the embedded `kit/v1/**` (except `bootstrap/`), and compute sha256 for
   each file into `kit.json` → `baseline`.
4. **Bootstrap (on by default; `--no-bootstrap` skips it):**
   - runs `claude -p` in the worktree with `--allowedTools Read Grep Glob "Bash(ls:*)" "Bash(cat:*)"`,
     `--permission-mode default` (**no writes**), a low `--max-turns` (e.g. 25), and the Phase 1
     profile;
   - the prompt asks it to inspect `*.sln*`, `package.json`, `Makefile`, CI files, `README`,
     `.editorconfig` and linters, and to return `KitPrefill` JSON only;
   - the output is parsed and validated against a schema. On a parse failure, keep the defaults and
     note it in the PR description;
   - merge: `context.md` sections are filled, `verify.*` is filled, conventions are appended to
     `reviewers/general.md`, and every inferred block is marked `<!-- inferred: please verify -->`;
   - recompute the `baseline` hashes **before** the prefill, so the prefilled files count as
     "customized" for a later upgrade.
5. **Validate** the result with `ValidateKit`; fix or drop the prefilled pieces that fail.
6. **Commit** (`chore(agentd): initialize ai-sdlc kit v1`), push, and open the PR through
   `IPullRequestService`. The description explains each file, what to customize first, and the
   inferred items to verify.
7. Announce it in chat (the repo's default provider) and return the PR URL. Remove the worktree.

## Tests
- On a fixture repo without a kit: the PR branch contains every kit file, `baseline` hashes match,
  and `ValidateKit` passes.
- `.agentd/` already on base → `KitExists`; an open init PR → its URL is returned and no new PR
  is made.
- Bootstrap with a recorded stream-json fixture: the prefill is merged and marked inferred.
  Malformed output → defaults are kept and noted.
- The bootstrap arguments contain no write tools (asserted on the built `ArgumentList`).

## Done when
- [ ] `agentd kit init <repo>` opens a Kit PR with a pre-filled `context.md` and verify commands
      (the exit criterion).
- [ ] The bootstrap cannot modify files (tool allowlist + test).
- [ ] The PR description lists each inferred item for verification.
