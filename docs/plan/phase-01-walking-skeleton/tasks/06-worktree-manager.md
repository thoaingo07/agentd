# T1.6 — Worktree manager and git push

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.2, T1.4 | M | `Agentd.Infrastructure.Git` |

## Goal
Keep a **managed bare clone** per registered repository, and give every job its own git worktree on
the branch `ai/<id>-<slug>`, created from `origin/<base>`.
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
2. **Managed bare clone** (`EnsureCloneAsync(repo)`, used by T1.4 and before each job):
   - `~/.agentd/repos/<host>/<org>/<project>/<repo>.git`, created with `git clone --bare <url>` and
     given the refspec `+refs/heads/*:refs/remotes/origin/*`, so `origin/<base>` exists;
   - `git fetch origin <base>` before each job; repeated jobs reuse the same clone.
3. **Create** (`CreateAsync(repo, branch)`):
   ```
   git -C <bareClone> worktree add ~/.agentd/worktrees/<repo>/wi-<id> -b <branch> origin/<base>
   ```
   - If the branch already exists (a retry): `worktree add <path> <branch>` without `-b`.
   - If the path already exists and is a registered worktree for that branch: reuse it.
4. **Commits ahead:** `git -C <path> rev-list --count <Remote>/<BaseBranch>..HEAD` > 0.
5. **Push:** `git -C <path> push -u origin <branch>`. **Never pass `--force`** (a guard throws if
   anything asks for it).
6. **Credentials** ([deployment.md §5](../../../architect/deployment.md#5-credentials-on-a-headless-server)):
   - every git call gets `GIT_SSH_COMMAND` pointing at **agentd's SSH key**
     (`~/.agentd/ssh/id_ed25519`) when it exists;
   - otherwise git uses the user's normal SSH config, e.g. the `erm-azdo` alias on this dev machine;
   - `GIT_TERMINAL_PROMPT=0` is always set.
7. **Remove** (`RemoveAsync(path)`): `git worktree remove --force <path>` (the path is only ours).
   Config `Git.KeepWorktrees` (default `false`) keeps them for debugging.
8. **Prune** on startup for every registered repo: `git -C <bareClone> worktree prune`.
9. **Concurrency:** a per-repository `SemaphoreSlim`, because git's own locks can fail under
   parallel `worktree add` on the same repo.

## Tests
- `Agentd.Infrastructure.Tests` with a temporary bare "remote" + agentd's managed bare clone of it:
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
