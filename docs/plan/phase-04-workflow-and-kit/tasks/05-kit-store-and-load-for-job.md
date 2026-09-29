# T4.5 — `IKitStore` + `LoadKitForJob` (base-branch snapshot, `RequireKit`)

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 4 | T4.4, Phase 1 (Git worktrees) | M | `Agentd.Application`, `Agentd.Infrastructure.Git` |

## Goal
When a job starts, read the repo's `.agentd/` kit **from the base branch** (never from the job's
working branch), layer it, validate it, and **snapshot** it into `job_artifacts`. The whole run uses
that snapshot, so kit edits on `main` affect the *next* job but never a running one.

## Files
- `src/Agentd.Application/Kits/IKitStore.cs` — create: port.
- `src/Agentd.Application/Kits/LoadKitForJob.cs` — create: the use case.
- `src/Agentd.Infrastructure.Git/Kit/GitKitStore.cs` — create: implementation.
- `src/Agentd.Domain/Kits/KitSnapshot.cs` — create: `{ KitVersion, Files (path → sha256), SettingsJson, SourceCommit }`.
- `src/Agentd.Host/Options/RepositoryOptions.cs` — modify: `RequireKit` (bool, default `true`).

## Implementation
1. Port:
   ```csharp
   public interface IKitStore
   {
       Task<Kit?> ReadFromBaseAsync(RepositoryRef repo, CancellationToken ct);   // null = no .agentd/
       Task<Kit> DefaultsAsync(KitVersion version, CancellationToken ct);        // embedded (T4.3)
       Task<Kit?> OrgOverridesAsync(CancellationToken ct);                       // <data dir>/kit-overrides/
   }
   ```
2. `GitKitStore.ReadFromBaseAsync`:
   - `git fetch origin <base>`;
   - `git ls-tree -r --name-only origin/<base> -- .agentd/`;
   - `git show origin/<base>:<path>` for each file;
   - the source commit comes from `git rev-parse origin/<base>`;
   - **no checkout**, so it works without touching any worktree.
3. `LoadKitForJob(jobId)`:
   1. read the repo kit; if it is missing and `RequireKit` is true → `Result.Fail(KitMissing)`. The
      caller (intake, T4.8) posts "This repo has no ai-sdlc kit; run *Initialize kit*" to chat and
      holds the job in `WaitingForHuman` (gate kind `KitRequired`);
   2. layer (T4.4), then validate. If invalid → `Result.Fail(KitInvalid, errors)`; errors go to chat,
      and the job waits;
   3. otherwise store a `job_artifacts` row with `kind = kit-snapshot` holding the settings JSON +
      file hashes + source commit, and the full file contents (compressed `jsonb` or `bytea`;
      typically < 100 KB);
   4. return the `Kit` + `PhasePolicy`.
4. Re-checking a waiting job: a small `KitWatcher` (in the host's scheduler loop, every poll)
   re-runs `LoadKitForJob` for jobs waiting on `KitRequired` or `KitInvalid`, so merging the Kit PR
   un-blocks them automatically.
5. Later phases always read files **from the snapshot**, never from disk.

## Tests
- Integration (a temporary git repo with a bare "origin"):
  - the kit on `main` is read correctly while the working branch has different `.agentd/` content
    (the snapshot uses `main`'s);
  - no `.agentd/` + `RequireKit` → `KitMissing`;
  - no `.agentd/` + `RequireKit: false` → built-in defaults are used;
  - an invalid kit → `KitInvalid` with its errors.
- Snapshot isolation: after snapshotting, changing `main` doesn't change what the job reads.
- `KitWatcher` un-blocks a waiting job once a valid kit lands on `main`.

## Done when
- [ ] Jobs never read kit files from a worktree (checked by a test with a mocked working tree).
- [ ] `RequireKit` behavior matches the exit criterion ("the job waits and says so in chat").
- [ ] The snapshot row exists for every started job and is visible in the Artifacts tab (T4.12).
