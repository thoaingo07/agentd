# agentd — Clean Architecture + BFF

This describes how the .NET 10 solution is structured. The **domain and use cases sit at the
center** and know nothing about Azure DevOps, Discord, Claude, PostgreSQL or HTTP. The browser
talks only to a **Backend-for-Frontend (BFF)**, which owns the session, the cookies and the
UI-shaped API, so **no token ever reaches the browser**.

Related: [Architecture](README.md) · [Security](../security/README.md) · [UI](../ui/README.md)

---

## 1. Layers and the dependency rule

```mermaid
flowchart TB
    subgraph P[Presentation — driving adapters]
        BFF[Agentd.Bff<br/>browser: endpoints, hub, auth,<br/>antiforgery, CSP, SPA hosting]
        MCP[Agentd.Mcp<br/>agents: ask_developer, finish, ...]
    end
    subgraph I[Infrastructure — driven adapters]
        PER[Persistence<br/>EF Core + Npgsql]
        ADO[AzureDevOps]
        DIS[Discord]
        CLA[Claude<br/>process runner]
        GIT[Git<br/>worktrees]
    end
    APP[Agentd.Application<br/>use cases + ports]
    DOM[Agentd.Domain<br/>Job aggregate, state machine, events]
    HOST[Agentd.Host<br/>composition root + workers]

    BFF --> APP
    MCP --> APP
    PER & ADO & DIS & CLA & GIT --> APP
    APP --> DOM
    HOST --> BFF & MCP & PER & ADO & DIS & CLA & GIT
```

**Rule:** source code dependencies point **inward only**.

| Project | May reference | Must not reference |
|---|---|---|
| `Agentd.Domain` | nothing (BCL only) | everything else, EF Core, ASP.NET Core |
| `Agentd.Application` | Domain, `Microsoft.Extensions.*.Abstractions` | Infrastructure, Presentation, EF Core, ASP.NET Core, Discord.Net |
| `Agentd.Infrastructure.*` | Application, Domain, its external SDK | Presentation, other Infrastructure projects |
| `Agentd.Bff`, `Agentd.Mcp` | Application, Domain (read-only types) | Infrastructure |
| `Agentd.Host` | everything | — |

This is enforced two ways: project references, and **architecture tests** (§7).

---

## 2. Domain (`Agentd.Domain`)

Pure C# with no framework attributes. EF mapping lives in Persistence.

- **Aggregate `Job`.** It owns the state machine from [Architecture §3.4](README.md#34-scheduler--supervisor-the-core).
  Transitions are methods, so an invalid transition cannot be expressed:

  ```csharp
  public sealed class Job : AggregateRoot<JobId>
  {
      public WorkItemId WorkItemId { get; }
      public JobState State { get; private set; }
      public ClaudeSessionId SessionId { get; }
      public BranchName Branch { get; }
      public int Attempt { get; private set; }

      public void Start(WorktreePath path, ChatThreadId thread) { Require(JobState.Preparing); ...; Raise(new JobStarted(Id)); }
      public void AskDeveloper(string question)                 { Require(JobState.Running); State = JobState.WaitingForHuman; Raise(new DeveloperQuestionAsked(Id, question)); }
      public void ResumeWith(DeveloperMessage reply)            { Require(JobState.WaitingForHuman, JobState.Running); ... }
      public void Finish(PullRequestDraft draft)                { Require(JobState.Running); State = JobState.Publishing; ... }
      public void Complete(PullRequestUrl url)                  { Require(JobState.Publishing); State = JobState.Done; ... }
      public void Fail(string reason) / Cancel(string by) / Retry()
  }
  ```

- **Value objects:** `WorkItemId`, `JobId`, `ClaudeSessionId`, `BranchName` (validated
  `ai/<id>-<slug>`), `WorktreePath`, `ChatThreadId`, `PullRequestUrl`, `Cost`, `TokenUsage`.
- **Domain events:** `JobQueued`, `JobStarted`, `DeveloperQuestionAsked`, `DeveloperReplied`,
  `JobFinished`, `PullRequestCreated`, `JobFailed`, `JobCancelled`. Persistence records them in the
  event log and publishes them after commit.
- **Domain services** (pure logic): `BranchNamer`, `RepositoryMatcher` (work item → repo rules).

---

## 3. Application (`Agentd.Application`)

Use cases plus the **ports** they need. There is **no mediator library**: handlers are plain
classes registered in DI, and endpoints and workers call them directly.

### 3.1 Use cases

| Kind | Use case | Called by |
|---|---|---|
| Command | `PollWorkItems` | Host worker (timer) |
| Command | `ClaimWorkItem` | `PollWorkItems`, BFF `POST /api/workitems/{id}/run`, Discord `/agentd run` |
| Command | `StartNextJob` | Host scheduler worker (when a slot is free) |
| Command | `RecordAgentOutput` | Claude runner (per stream-json line) |
| Command | `AskDeveloper` / `ReportProgress` / `FinishWork` | MCP tools |
| Command | `SubmitDeveloperMessage` | Discord inbound, BFF `POST /api/jobs/{id}/messages` |
| Command | `PublishPullRequest` | after `FinishWork` |
| Command | `CancelJob` / `RetryJob` | BFF, Discord |
| Command | `RecoverJobsOnStartup` | Host startup |
| Query | `GetDashboard`, `GetJob`, `GetJobEvents`, `GetJobDiff`, `SearchHistory` | BFF |

```csharp
public interface ICommandHandler<in TCommand, TResult> { Task<Result<TResult>> Handle(TCommand command, CancellationToken ct); }
public interface IQueryHandler<in TQuery, TResult>     { Task<TResult> Handle(TQuery query, CancellationToken ct); }
```

- Handlers return `Result<T>` (success or a typed `Error`) for expected failures, such as
  `InvalidTransition`, `NotFound` or `AlreadyClaimed`. Exceptions are for bugs and infrastructure faults.
- Cross-cutting concerns (logging, tracing, transaction) are DI **decorators** around handlers,
  not framework pipelines.

### 3.2 Ports (interfaces implemented by Infrastructure)

| Port | Implemented in |
|---|---|
| `IJobRepository`, `IUnitOfWork` | Persistence |
| `IEventStore` (append, read after/before seq), `IEventPublisher` (live fan-out) | Persistence (+ `LISTEN/NOTIFY`) |
| `IWorkItemSource` (query, get, claim, comment) | AzureDevOps |
| `IPullRequestService` (push, create PR) | AzureDevOps + Git |
| `IChatChannel` (create thread, post, post file) | Discord |
| `IAgentRunner` (start/resume a session, cancel) | Claude |
| `IWorktreeManager` (create, remove, diff, prune) | Git |
| `IClock`, `IIdGenerator` | Host (defaults) |

The Application layer also defines **read models** returned by queries, such as `JobSummary`,
`JobDetail`, `AgentEventDto` and `DashboardStats`. They are plain records that the BFF maps to its
own view models.

---

## 4. Infrastructure (`Agentd.Infrastructure.*`)

One project per external system, so that a dependency (e.g. Discord.Net) stays in one place:

| Project | Contents |
|---|---|
| `Infrastructure.Persistence` | `AgentdDbContext`, Fluent configurations, migrations, repositories, event store (partitioned `events` table), outbox/`NOTIFY` publisher, `FOR UPDATE SKIP LOCKED` dequeue |
| `Infrastructure.AzureDevOps` | `AzureCliCredential` / PAT token providers, typed `HttpClient`s (WIQL, work items, comments, PRs), resilience |
| `Infrastructure.Discord` | `DiscordSocketClient` hosted service, `IChatChannel`, slash commands. Inbound messages call Application commands (`SubmitDeveloperMessage`, `CancelJob`, …). |
| `Infrastructure.Claude` | `ClaudeProcessRunner` (`ProcessStartInfo.ArgumentList`, secret-stripped env), stream-json parser → `RecordAgentOutput` |
| `Infrastructure.Git` | `git` CLI wrapper for worktrees, push, diff |

Each project exposes a single `AddXxx(this IServiceCollection, IConfiguration)` extension, which the
Host calls.

---

## 5. Presentation

### 5.1 `Agentd.Bff`: Backend-for-Frontend (browser)

The Vue SPA talks **only** to the BFF, on the **same origin**. The BFF is the sole owner of browser
concerns:

| Responsibility | Details |
|---|---|
| **Session & auth** | Discord OAuth2 login handled server-side; the session is an HttpOnly cookie. **No access or refresh tokens in the browser.** Downstream credentials (ADO token/PAT, Discord bot token) never leave the server. |
| **Session endpoints** (`/bff/*`) | `GET /bff/login?returnUrl=` (challenge → Discord), `POST /bff/logout`, `GET /bff/user` (the current user, or 401), `GET /bff/antiforgery` (request token in the body) |
| **UI-shaped API** (`/api/*`) | one endpoint per screen need, returning **view models**, not domain or Application types: `GET /api/dashboard` (stats + active jobs in one call), `GET /api/jobs/{id}`, `GET /api/jobs/{id}/events`, `GET /api/jobs/{id}/diff`, `GET /api/history`, `POST /api/jobs/{id}/cancel`, … |
| **Real-time** | SignalR hub `/hubs/events` (read-only subscribe), fed by `IEventPublisher` |
| **Protection** | antiforgery filter on `/api` and `/bff` unsafe methods; CSP and security headers; Origin check on the hub ([Security](../security/README.md)) |
| **SPA hosting** | `MapStaticAssets()` + `MapFallbackToFile("index.html")` |
| **API contract** | the OpenAPI document is generated from BFF endpoints only; `web/` generates its TS types from it |

Endpoints stay thin: *bind → call the use case → map the `Result` to HTTP / view model*.

```csharp
public static class JobEndpoints
{
    public static RouteGroupBuilder MapJobEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async (IQueryHandler<GetDashboard, DashboardReadModel> q, CancellationToken ct)
            => TypedResults.Ok(DashboardVm.From(await q.Handle(new(), ct))));

        api.MapPost("/jobs/{id:long}/cancel", async (long id, ClaimsPrincipal user,
                ICommandHandler<CancelJob, Unit> h, CancellationToken ct)
            => (await h.Handle(new CancelJob(new JobId(id), user.DisplayName()), ct)).ToHttpResult());
        return api;
    }
}
```

**Why a BFF here:** the browser gets exactly the data each screen needs in one round trip, all
credentials and third-party calls stay server-side, the cookie-plus-antiforgery model stays simple
(same origin, no CORS), and UI-driven API changes never leak into the Application layer.

**In-process today, splittable later.** The BFF runs in the same process as the daemon and calls
use cases directly. Because it depends only on Application interfaces, it can later move to its
own process (with the Application layer reached through PostgreSQL plus `LISTEN/NOTIFY`, or a small
internal API) without touching the Domain or Application code.

### 5.2 `Agentd.Mcp`: agent-facing adapter

MCP tools (`ask_developer`, `report_progress`, `finish`, `get_work_item`) map 1:1 to Application
commands. They use their own auth scheme (a per-job bearer token), and they share no cookies,
antiforgery setup or view models with the BFF.

---

## 6. Host (`Agentd.Host`): composition root

- `Program.cs` wires everything: `AddDomain()`, `AddApplication()`, `AddPersistence()`,
  `AddAzureDevOps()`, `AddDiscord()`, `AddClaude()`, `AddGit()`, `AddBff()`, `AddMcp()`.
- **Workers** (`BackgroundService`, driving adapters triggered by time):
  `WorkItemPollingWorker` → `PollWorkItems`, `SchedulerWorker` → `StartNextJob`,
  `RetentionWorker` → drops old event partitions, and `StartupRecovery` → `RecoverJobsOnStartup`.
- Holds `wwwroot/` (the Vite build output), `appsettings*.json`, OpenTelemetry and health checks.
- Contains no business logic.

---

## 7. Tests

| Project | Scope |
|---|---|
| `Agentd.Domain.Tests` | the state machine, value object validation, domain events. Pure, fast. |
| `Agentd.Application.Tests` | handlers with in-memory fakes of the ports |
| `Agentd.Infrastructure.Tests` | Persistence against real PostgreSQL (Testcontainers); the stream-json parser against recorded fixtures; ADO clients against recorded HTTP |
| `Agentd.Bff.Tests` | `WebApplicationFactory`: auth, antiforgery (unsafe methods without the header → 400), CSP header present, view-model shape |
| `Agentd.ArchitectureTests` | enforces §1. For example: Domain has no reference outside the BCL; Application does not reference `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore.*` or `Discord`; Bff/Mcp do not reference `Agentd.Infrastructure.*`. |

---

## 8. Where each component from the architecture lives

| Architecture component | Layer / project |
|---|---|
| Config | Host (`IOptions<T>` classes live in the project that consumes them) |
| Azure DevOps client | `Infrastructure.AzureDevOps` behind `IWorkItemSource` / `IPullRequestService` |
| Work Item Poller | Host worker → `PollWorkItems` use case |
| Scheduler / Supervisor | state machine in Domain `Job`; orchestration in Application handlers; timer in a Host worker |
| Worktree Manager | `Infrastructure.Git` behind `IWorktreeManager` |
| Agent Runner | `Infrastructure.Claude` behind `IAgentRunner` |
| agentd MCP server | `Agentd.Mcp` (Presentation) |
| Discord Gateway | `Infrastructure.Discord` (`IChatChannel` outbound; inbound calls Application commands) |
| Event Bus | `IEventStore` / `IEventPublisher` ports → Persistence + in-process channels |
| Web Server / Web UI | `Agentd.Bff` + `web/` |
| PR Publisher | `PublishPullRequest` use case → `IPullRequestService` |
