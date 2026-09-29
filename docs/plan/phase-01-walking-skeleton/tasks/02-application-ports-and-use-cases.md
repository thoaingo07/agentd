# T1.2 — Application: ports and use cases

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.1 | M | `Agentd.Application` |

## Goal
Define the use cases of the walking skeleton and the ports (interfaces) they need. The use cases
contain the orchestration logic and stay framework-free. The Infrastructure tasks (T1.3–T1.10)
implement the ports.

## Files
- `src/Agentd.Application/Abstractions/ICommandHandler.cs`, `IQueryHandler.cs` — create.
- `src/Agentd.Application/Ports/*.cs` — create: `IJobRepository`, `IUnitOfWork`, `IEventStore`, `IWorkItemSource`, `IPullRequestService`, `IWorktreeManager`, `IAgentRunner`, `IRepositoryRegistry`, `IMcpTokenIssuer`.
- `src/Agentd.Application/Jobs/*.cs` — create: one file per use case (command + handler).
- `src/Agentd.Application/DependencyInjection.cs` — create: `AddApplication()`.
- `tests/Agentd.Application.Tests/Jobs/*Tests.cs` — create.

## Implementation
1. **Handler abstractions** (no mediator library):
   ```csharp
   public interface ICommandHandler<in TCommand, TResult> { Task<Result<TResult>> Handle(TCommand c, CancellationToken ct); }
   public interface IQueryHandler<in TQuery, TResult>     { Task<TResult> Handle(TQuery q, CancellationToken ct); }
   ```
2. **Ports** (the signatures to implement):
   - `IWorkItemSource`:
     - `QueryTaggedAsync(tag, excludeTag, states)` → `IReadOnlyList<WorkItemRef{Id, Rev}>`
     - `GetAsync(id)` → `WorkItemDetails` (title, description, acceptance criteria, repro steps,
       area path, tags, comments)
     - `TryClaimAsync(id, rev, claimTag)` → `bool` (false on a revision conflict)
     - `AddCommentAsync(id, text)`
   - `IPullRequestService`:
     - `FindOpenAsync(repo, sourceBranch)` → `PullRequestRef?`
     - `CreateAsync(repo, source, target, title, description, workItemId)` → `PullRequestRef`
   - `IWorktreeManager`:
     - `CreateAsync(repo, branch)` → `WorktreePath`
     - `HasCommitsAheadAsync(path, baseBranch)`
     - `PushAsync(path, branch)`
     - `RemoveAsync(path)`
     - `PruneAsync(repo)`
   - `IAgentRunner`:
     - `RunAsync(AgentRunRequest{JobId, WorktreePath, SessionId, Prompt, Resume}, ct)` → `AgentRunOutcome`
       (`Exited(code)` | `TimedOut` | `Cancelled`)
     - `IsRunning(jobId)`
     - `Cancel(jobId)`
   - `IJobRepository`:
     - `GetAsync(id)`, `FindActiveByWorkItemAsync(wi)`, `AddAsync(job)`
     - `DequeueNextAsync(ct)` (the lock-and-take is implemented in T1.3)
     - `ListByStateAsync(states)`
   - `IEventStore.AppendAsync(jobId, type, payloadJson)`.
   - `IRepositoryRegistry.Match(WorkItemDetails)` → `RepositoryConfig?`
3. **Use cases** (commands unless noted):
   - `PollWorkItems` → the source query → for each item not already active → `ClaimWorkItem`.
   - `ClaimWorkItem(workItemId, force)`:
     - match the repo;
     - `TryClaimAsync` (skip if lost);
     - `Job.Create` → save;
     - comment "agentd picked this up".
   - `StartNextJob`:
     - dequeue → `BeginPreparing`;
     - create the worktree;
     - `Start`, then build the prompt from the work item;
     - run the agent **in the background** (the runner owns the process; the outcome is fed back
       through `HandleAgentExit`).
   - `HandleAgentExit(jobId, outcome)`: if the job is still `Running` after the process exits
     without calling `finish`, → `Fail("agent exited without finish")`.
   - `RecordAgentOutput(jobId, line)` → `IEventStore.AppendAsync`.
   - `FinishWork(jobId, draft)` → `Job.Finish` → trigger `PublishPullRequest`.
   - `PublishPullRequest(jobId)`: see T1.10.
   - `CancelJob(jobId, by)` → `IAgentRunner.Cancel` → `Job.Cancel`.
   - `RecoverJobsOnStartup`: see T1.11.
   - Query `GetJobStatus` for the CLI (T1.12).
4. **Prompt builder** (`TaskPromptBuilder`): renders the work item into a Markdown task prompt, plus
   fixed rules: work only in this repo, commit with clear messages, call `finish` when done.
5. **Transactions:** a handler usually needs **one** `IJobRepository.SaveAsync(job)`. It calls
   `agentd.job_save`, which writes the state **and** appends the domain events atomically (T1.3).
   When a use case must change several things atomically, it uses `IUnitOfWork`
   (`BeginAsync` / `CommitAsync`), which wraps one `NpgsqlTransaction` that the repositories share
   for that call only. No ORM change tracking is involved.

## Tests
- `Agentd.Application.Tests` with in-memory fakes for every port:
  - claim-conflict skip;
  - no repo match → work item comment, no job created;
  - `FinishWork` on a non-Running job → error;
  - `HandleAgentExit` without finish → `Failed`;
  - `CancelJob` calls the runner.

## Done when
- [ ] Application references only Domain + `Microsoft.Extensions.*.Abstractions` (architecture test green).
- [ ] Every use case has at least one happy-path and one failure test using fakes.
