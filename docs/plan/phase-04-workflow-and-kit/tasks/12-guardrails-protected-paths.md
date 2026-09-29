# T4.12 — Guardrails: deny `.agentd/**` and `paths.protected` to agents

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 4 | T4.5, T4.8 | S | `Agentd.Infrastructure.Claude`, `Agentd.Infrastructure.Orchestration` |

## Goal
Agents must not edit their own instructions (`.agentd/**`) or the paths the team marked as
protected. Enforce this in three places: the **Claude permission rules** (prevent), a **diff check**
before phase completion (detect), and the **reviewer** (flag)
([ai-sdlc-kit.md §4](../../../architect/ai-sdlc-kit.md#4-guardrails)).

## Files
- `src/Agentd.Infrastructure.Claude/PermissionSettingsBuilder.cs` — create: generates the per-job settings with deny rules.
- `src/Agentd.Infrastructure.Claude/ClaudeCodeRunner.cs` — modify: passes the generated settings (`--settings <file>`) plus `--disallowedTools` entries.
- `src/Agentd.Infrastructure.Orchestration/Executors/ProtectedPathsCheck.cs` — create: diff check before `complete_phase` / publish.
- `kit/v1/reviewers/general.md` — modify: "flag any change under `.agentd/` or protected paths as blocking".

## Implementation
1. **Prevent.** Build a per-job settings file under `~/.agentd/runs/<job>/settings.json`, with
   `permissions.deny` entries for `Edit` and `Write` on `.agentd/**` and each `paths.protected` glob,
   plus `Bash` patterns that write to them where they can be expressed:
   ```json
   { "permissions": { "deny": [
       "Edit(.agentd/**)", "Write(.agentd/**)",
       "Edit(infra/prod/**)", "Write(infra/prod/**)"
   ] } }
   ```
   Confirm the exact rule syntax against the Claude Code settings docs for the version in use.
   Pass it with `--settings`. Keep the existing allowlist as is.
2. **Detect.** Bash can still write indirectly (e.g. `sed -i`), so run `ProtectedPathsCheck` before
   accepting `complete_phase` (Implement, Test) and before publish:
   - `git diff --name-only <base>...HEAD` plus uncommitted changes;
   - any match with `.agentd/**` or `paths.protected` → **revert those files**
     (`git checkout <base> -- <paths>` plus a fix-up commit), then return the tool error "Changes to
     protected paths were reverted: …" so the agent adapts;
   - record a `guardrail.violation` event (visible in the Web UI and chat).
3. **Flag.** The general reviewer instruction treats any remaining change to these paths as
   blocking.
4. `.agentd-run/` (artifacts) is **not** protected, because agentd writes it, and it is git-excluded.

## Tests
- `PermissionSettingsBuilder` output contains a deny rule for `.agentd/**` and each protected glob.
- Integration with a fake runner that "edits" `.agentd/phases/implement.md` through a shell write:
  the check reverts the file, the tool returns the error, and a `guardrail.violation` event is
  recorded.
- Publish is blocked if a protected change reaches it (defense in depth).
- A real Claude smoke test (manual, recorded in the PR): asking the agent to edit
  `.agentd/phases/implement.md` gets denied.

## Done when
- [ ] Exit criterion: an agent attempt to edit `.agentd/phases/implement.md` is denied or reverted,
      with a visible event.
- [ ] Protected globs from `kit.json` are honored the same way.
- [ ] No published branch ever contains changes to `.agentd/**` from a job (a test on publish).
