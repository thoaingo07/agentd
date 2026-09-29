# T1.4 — Repository registration and work item → repo matching

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.2, Phase 0 (options) | S | `Agentd.Application` + `Agentd.Host` |

## Goal
agentd must know which local repository a work item belongs to. Repositories are registered in
the operator config, and each work item is matched by **area path** or a **`repo:<name>` tag**.

## Files
- `src/Agentd.Application/Repositories/RepositoryConfig.cs` — create: the options record.
- `src/Agentd.Application/Repositories/RepositoryRegistry.cs` — create: `IRepositoryRegistry`.
- `src/Agentd.Host/Options/AgentdOptions.cs` — modify: add `Repositories`.
- `src/Agentd.Host/appsettings.json` — modify: an example entry.
- `tests/Agentd.Application.Tests/Repositories/RepositoryRegistryTests.cs` — create.

## Implementation
1. **Config shape** (operator-owned; see architecture §3.1 and ai-sdlc-kit §5):
   ```jsonc
   "Agentd": {
     "Repositories": [
       {
         "Name": "agentd",
         "LocalPath": "/home/ulab/tngo/github/agentd",
         "Remote": "origin",
         "BaseBranch": "main",
         "AdoRepository": "agentd",                      // ADO repo name or ID, used for PR creation
         "Match": { "AreaPaths": ["MyProject\\Platform"], "Tag": "repo:agentd" },
         "Limits": { "MaxTurns": 200 }
       }
     ]
   }
   ```
2. **Validation at startup** (`IValidateOptions`):
   - names are unique;
   - `LocalPath` exists and is a git repo (`.git` present);
   - `BaseBranch` is not empty;
   - at least one match rule is set.
3. **Matching order:**
   1. an exact `repo:<name>` tag on the work item;
   2. **the longest** area path prefix match (case-insensitive, `\` separators);
   3. none → `null`.

   If several repos match at the same precedence level, the result is `Ambiguous` and the claim is
   refused with a clear work item comment.
4. **No-match handling** (in `ClaimWorkItem`): comment once on the work item ("agentd: no repository
   matches this item; add a `repo:<name>` tag"), then remember it in memory so it isn't re-commented
   on every poll. Don't claim it.

## Tests
- `Agentd.Application.Tests`:
  - tag beats area path;
  - the longest area-path prefix wins;
  - case-insensitivity;
  - ambiguous → no claim + one comment;
  - no match → no claim + one comment;
  - an invalid config fails validation with readable messages.

## Done when
- [ ] Startup fails fast with a clear message for invalid repository config.
- [ ] Matching is covered by tests for tags, area paths, ambiguity and no match.

## Open decision
- Which rule should be **primary** in your org: area path or the `repo:` tag? (Architecture decision #4.) Both are implemented; the tag wins when present.
