# T1.6 — Worktree manager and git push

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.2, T1.4 | M | `Agentd.Infrastructure.Git` |

## Goal
Give every job its own git worktree on the branch `ai/<id>-<slug>`, created from `origin/<base>`.
Detect commits, push, and clean up. Wraps the `git` CLI safely.

## Files
- `src/Agentd.Infrastructure.Git/GitCli.cs` — create: runs `git` with `ArgumentList`, captures output, applies timeouts.
- `src/Agentd.Infrastructure.Git/WorktreeManager.cs` — create: `IWorktreeManager`.
- `src/Agentd.Infrastructure.Git/GitOptions.cs`, `DependencyInjection.cs` — create.
- `tests/Agentd.Infrastructure.Tests/Git/WorktreeManagerTests.cs` — create (uses temporary local repos).

## Implementation
1. **`GitCli.RunAsync(workingDir, params string[] args)`:**
   - `ProcessStartInfo` with `ArgumentList` (never a shell string);
   - environment `GIT_TERMINAL_PROMPT=0`;
   - a 5-minute default timeout;
   - returns `(exitCode, stdout, stderr)`; a non-zero exit → `GitException` carrying stderr.
2. **Create** (`CreateAsync(repo, branch)`):
   ```
   git -C <LocalPath> fetch <Remote> <BaseBranch>
   git -C <LocalPath> worktree add <WorktreeRoot>/<repo>/wi-<id> -b <branch> <Remote>/<BaseBranch>
   ```
   - If the branch already exists (a retry): `worktree add <path> <branch>` without `-b`.
   - If the path already exists and is a registered worktree for that branch: reuse it.
   - `WorktreeRoot` defaults to `~/.agentd/worktrees` (expand `~`).
3. **Commits ahead:** `git -C <path> rev-list --count <Remote>/<BaseBranch>..HEAD` > 0.
4. **Push:** `git -C <path> push -u <Remote> <branch>`. **Never pass `--force`** (a guard throws if
   anything asks for it). Credentials come from the host's git configuration for the repo (SSH key
   or credential manager); agentd doesn't handle them in Phase 1.
5. **Remove** (`RemoveAsync(path)`): `git worktree remove --force <path>` (the path is only ours).
   Config `Git.KeepWorktrees` (default `false`) keeps them for debugging.
6. **Prune** on startup for every registered repo: `git -C <LocalPath> worktree prune`.
7. **Concurrency:** a per-repository `SemaphoreSlim`, because git's own locks can fail under
   parallel `worktree add` on the same repo.

## Tests
- `Agentd.Infrastructure.Tests` with a temporary bare "remote" + a clone:
  - create → the branch exists and starts at `origin/main`;
  - create twice (retry) reuses it;
  - commits-ahead false → true after a commit;
  - push lands on the bare remote;
  - remove + prune leave no registered worktree;
  - two parallel creates on the same repo both succeed.
- A guard test: requesting a force push throws.

## Done when
- [ ] Two jobs on the same repo get separate worktrees and branches and don't interfere.
- [ ] No shell string building anywhere in `GitCli` (code review + test).
