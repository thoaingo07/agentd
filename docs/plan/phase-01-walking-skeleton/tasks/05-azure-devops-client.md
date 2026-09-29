# T1.5 — Azure DevOps client (az CLI + PAT auth, work items, PRs)

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.2 | L | `Agentd.Infrastructure.AzureDevOps` |

## Goal
Implement `IWorkItemSource` and the ADO half of `IPullRequestService` over the REST API
(`api-version=7.1`), with interchangeable **az CLI** and **PAT** authentication.

## Files
- `src/Agentd.Infrastructure.AzureDevOps/Auth/IAzureDevOpsTokenProvider.cs` — create.
- `src/Agentd.Infrastructure.AzureDevOps/Auth/AzureCliTokenProvider.cs` — create (`Azure.Identity`).
- `src/Agentd.Infrastructure.AzureDevOps/Auth/PatTokenProvider.cs` — create.
- `src/Agentd.Infrastructure.AzureDevOps/Auth/AuthHeaderHandler.cs` — create: `DelegatingHandler`.
- `src/Agentd.Infrastructure.AzureDevOps/WorkItems/WorkItemClient.cs` — create: `IWorkItemSource`.
- `src/Agentd.Infrastructure.AzureDevOps/PullRequests/PullRequestClient.cs` — create.
- `src/Agentd.Infrastructure.AzureDevOps/AzureDevOpsOptions.cs`, `DependencyInjection.cs` — create.
- `tests/Agentd.Infrastructure.Tests/AzureDevOps/*` — create, with recorded JSON fixtures.

## Implementation
1. **Options:** `Organization`, `Project`, `Auth` (`AzCli` | `Pat`), `Tag`, `ClaimTag`, `States`,
   `PollInterval`. The PAT comes from `Agentd__AzureDevOps__Pat` (env or user-secrets only).
2. **Auth providers:**
   - `AzCli`: `new AzureCliCredential().GetTokenAsync(new(["499b84ac-1321-427f-aa17-267ca6975798/.default"]))`.
     Cache the token until `ExpiresOn - 5 min`.
   - `Pat`: `Basic base64(":" + pat)`.
   - `AuthHeaderHandler` adds the header per request, and on a 401 (az CLI mode) refreshes once
     before failing.
3. **Typed `HttpClient`** with `AddStandardResilienceHandler()`. Honor 429 `Retry-After`. Never log
   the auth header.
4. **Work items:**
   - `QueryTaggedAsync`: WIQL `POST {project}/_apis/wit/wiql` (query in the ADO reference). The tag,
     claim tag and states come from options and are escaped (single quotes doubled).
   - `GetAsync`: `GET _apis/wit/workitems/{id}?$expand=all` + comments
     `GET {project}/_apis/wit/workItems/{id}/comments?api-version=7.1-preview.4`. Strip HTML to
     text for the prompt, and keep the raw HTML for later phases.
   - `TryClaimAsync`: JSON Patch with `{op:"test", path:"/rev", value:rev}` + add the tag
     (keep the existing tags, `; ` separated). A 412 or conflict → `false`.
   - `AddCommentAsync`: POST a comment (HTML-escape the text).
5. **Pull requests** (REST, decision pending; see Open decision):
   - `FindOpenAsync`: `GET …/pullrequests?searchCriteria.sourceRefName=refs/heads/{branch}&searchCriteria.status=active`.
   - `CreateAsync`: `POST …/pullrequests` with `workItemRefs:[{id}]`. Return the PR ID + web URL
     (`_links.web.href`).
6. **Errors** map to typed exceptions (`AdoAuthException`, `AdoNotFoundException`,
   `AdoConflictException`), which the Application use cases handle.

## Tests
- `Agentd.Infrastructure.Tests` with a fake `HttpMessageHandler` replaying **recorded fixtures**:
  - the WIQL request body (escaping);
  - the claim patch body contains the `/rev` test;
  - a 412 → `TryClaimAsync` returns false;
  - PR creation includes `workItemRefs`;
  - a 401 refresh path;
  - PAT header format;
  - the PAT never appears in logs (capture the logger).
- **Manual smoke test** (documented in `docs/ops/ado-smoke.md`): list, claim, comment and create a
  PR against the sandbox project.

## Done when
- [ ] Both auth modes work against the sandbox organization.
- [ ] All fixture tests pass; no secret appears in logs.

## Open decisions
- Sandbox organization, project and repo for tests and demos.
- **PR creation:** the REST API (planned) or `az repos pr create`? REST is assumed here.
