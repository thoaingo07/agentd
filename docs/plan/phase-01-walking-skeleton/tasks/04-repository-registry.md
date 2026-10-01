# T1.4 — Repository registry (any repo, managed clones) and work item → repo matching

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.2, T1.3 | M | `Agentd.Domain`, `Agentd.Application`, `Agentd.Migrator`, `Agentd.Infrastructure.Persistence` |

## Goal
agentd works on **any repository**, registered by URL, not by a local path
([deployment.md §3](../../../architect/deployment.md#3-any-repository-managed-clones)).
Registrations are stored in the database, and the config file can seed them. Each work item is
matched to a repository by a **`repo:<name>` tag** (wins) or else by **area path** (decided).

## Files
- `src/Agentd.Domain/Repositories/Repository.cs`: create. Name, `RemoteUrl`, provider coordinates
  (`AzureDevOpsRepo(org, project, repo)`), `BaseBranch`, match rules, limits.
- `src/Agentd.Domain/Repositories/RemoteUrl.cs`: create. Parses Azure DevOps SSH/HTTPS URLs
  (`git@ssh.dev.azure.com:v3/{org}/{project}/{repo}`, `{org}@vs-ssh.visualstudio.com:v3/…`,
  `https://dev.azure.com/{org}/{project}/_git/{repo}`, `https://{org}.visualstudio.com/{project}/_git/{repo}`)
  and SSH host aliases (`git@erm-azdo:v3/…`).
- `src/Agentd.Application/Repositories/IRepositoryRegistry.cs`, `RepositoryMatcher.cs`, `AddRepository.cs`: create.
- `src/Agentd.Migrator/Migrations/{version}_repositories.up.sql` + `Routines/repository/*.sql`: create.
- `src/Agentd.Infrastructure.Persistence/Repositories/RepositoryStore.cs`: create.
- `tests/Agentd.Domain.Tests/Repositories/RemoteUrlTests.cs`, `tests/Agentd.Application.Tests/Repositories/*`: create.

## Implementation
1. **Table `agentd.repositories`:** `name` (PK), `remote_url`, `provider`, `organization`,
   `project`, `repo`, `base_branch`, `match_tag`, `match_area_paths text[]`, `limits jsonb`,
   `enabled`, `created_at`, `version`. Routines: `repository_upsert`, `repository_get`,
   `repository_list`, `repository_remove`.
2. **`AddRepository(url, name?, baseBranch?, tag?, areaPaths?)`:**
   - parse the URL, and default the name to the repo name;
   - if no base branch is given, **detect it** from the remote HEAD via `IGitRemote.GetDefaultBranchAsync`
     (for `sysmin` that's `develop`);
   - upsert the registration, then ensure the bare clone exists (T1.6).
3. **Config seeding:** `Agentd:Repositories[]` entries (`Url`, optional `Name`, `BaseBranch`,
   `Match`) are upserted on startup, so a declarative setup still works.
4. **Matching order:**
   1. an exact `repo:<name>` tag on the work item;
   2. the **longest** area-path prefix match (case-insensitive, `\` separators);
   3. none → no match.

   Several matches at the same level → `Ambiguous`. On no match or ambiguity, comment **once** on the
   work item ("agentd: no repository matches; add a `repo:<name>` tag") and don't claim it.
5. **Development sandbox** (appsettings.Development.json seed):
   `{ "Url": "git@erm-azdo:v3/ermsystem/Portal/sysmin", "Match": { "Tag": "repo:sysmin" } }`.

## Tests
- `RemoteUrl`: every URL form above, including the `erm-azdo` alias; invalid URLs are rejected.
- The matcher: the tag beats the area path; the longest prefix wins; case-insensitivity; ambiguous and
  no match → no claim + exactly one comment.
- `AddRepository` with a fake `IGitRemote` detects `develop`; an explicit `--base` wins.
- Store round-trip against PostgreSQL (Integration).

## As built
- The config seed (`RepositorySeedOptions`, `Agentd:Repositories:Items`) is defined here and applied
  on startup by the workers (T1.11).
- Without a tag or area path, `repo add` defaults the match rule to the tag `repo:<name>`.
- `repository_remove` is a soft delete (`enabled = false`), so job history keeps its repository name.

## Done when
- [ ] `agentd repo add <url>` (T1.12) registers a repo and detects its default branch.
- [ ] Matching is covered by tests for tags, area paths, ambiguity and no match.
