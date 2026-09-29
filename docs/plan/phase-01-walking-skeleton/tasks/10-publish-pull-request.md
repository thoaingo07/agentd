# T1.10 — `PublishPullRequest` (idempotent)

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.2, T1.5, T1.6, T1.9 | M | `Agentd.Application` |

## Goal
After `finish`, turn the job's branch into a PR linked to the work item, then comment on the work
item. Running it twice (after a crash or retry) must not create duplicates.

## Files
- `src/Agentd.Application/Jobs/PublishPullRequest.cs` — create: command + handler.
- `src/Agentd.Application/Jobs/PullRequestDescriptionBuilder.cs` — create.
- `tests/Agentd.Application.Tests/Jobs/PublishPullRequestTests.cs` — create.

## Implementation
1. **Load** the job and require `Publishing`.
2. **No commits** (`HasCommitsAheadAsync` is false) → `Fail("no changes were committed")` + a work
   item comment. Stop.
3. **Push** the branch (`IWorktreeManager.PushAsync`). Pushing again is a no-op if nothing changed.
4. **Existing PR?** `FindOpenAsync(repo, branch)`. If one exists, reuse it (idempotency). Otherwise
   `CreateAsync(...)`:
   - **title:** `draft.Title` with `(WI-<id>)` appended if missing;
   - **description:** the agent's `pr_description` + a footer with the work item link, the job ID
     and "Created by agentd";
   - **target:** the repo's `BaseBranch`; **work item link:** `workItemRefs`.
5. **Comment** on the work item: "agentd opened PR !<id>: <url>". It includes a marker
   `<!-- agentd:pr:<prId> -->` so a retry can detect that the comment already exists and skip it.
6. `Job.Complete(prUrl)` → save.
7. **Cleanup:** remove the worktree (unless `KeepWorktrees`), and revoke the MCP token.
8. **Failures:**
   - a push or PR API error leaves the job in `Publishing`, with `LastError` set;
   - the scheduler retries publishing with backoff (3 attempts), then calls `Fail`.

## Tests
- Fakes:
  - the happy path (push → create → comment → Done);
  - no commits → Failed + comment;
  - a PR already exists → no second PR, still Done;
  - the comment marker already exists → no second comment;
  - a push failure → stays Publishing with the error, and the retry succeeds.

## Done when
- [ ] Running publish twice for the same job creates exactly one PR and one comment (a test).
- [ ] The sandbox demo shows a PR linked to the work item, and the work item has a comment with the PR link.
