# agentd — Incident monitor (Application Insights)

> Decided 2026-10-14. It replaces the PR Monitor's place: agentd watches **running apps**, not PRs.
> Built in steps:
> - **PR 1 detects incidents and alerts on them.** It's split in two to stay small: **1a** has the incident types,
>   options, tables, routines and store; **1b** has the monitor pass, the Application Insights source, the worker and
>   the alerts.
> - **PR 2** investigates and offers **Fix it**, also in two parts: **2a** investigates; **2b** adds Fix it, Ignore,
>   questions in the thread and Resolved.
> - **PR 3** adds the web pages.
>
> The PR 3 part below is marked *next*.

agentd reads each configured app's **Application Insights** telemetry for exceptions and failed requests. It groups
them into **incidents**, one per bug however often it happens, and alerts in a 🚨 thread on the chat channel, or in the
Inbox when there is no chat. An agent then investigates each incident against the app's repository, and on
**Fix it** agentd files an Azure DevOps Bug for the normal job pipeline: plan, implement, verify, PR, review loop.

Decisions made with the user:

| Question | Decision |
|---|---|
| What triggers an incident | agentd **queries the telemetry itself** (exceptions, failed requests). It does not use Azure Monitor alert rules, so the only Azure setup is read access, and it works behind Tailscale with no inbound webhook. |
| How a fix reaches a PR | agentd **creates a work item** (a Bug with the analysis, tagged for agentd). The normal job runs, which keeps it traceable on the board. |
| When fixing starts | **A person asks first**: the alert and the investigation are automatic, and **Fix it** (a button or a reply) files the work item. |

## 1. Configuration (`Agentd:Monitor`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Run the monitor. When it's on, `Apps` must list at least one app. |
| `PollInterval` | `00:05:00` | How often each app is read (at least a minute). |
| `FirstWindow` | `00:15:00` | How far back the first pass looks. Later passes start where the last ended, and read at most a day. |
| `MinCount` | `3` | A new error group becomes an incident at this many occurrences in one pass. A known incident always counts. |
| `MaxNewPerPass` | `5` | At most this many alerts per pass across all apps. The rest are recorded without an alert. |
| `TenantId` | (default directory) | The Entra tenant to sign in to. |
| `Apps:N:Name` | | The app's name in alerts. Unique; letters, digits, `.`, `_` and `-`. |
| `Apps:N:ResourceId` | | The Application Insights component's ARM id: `/subscriptions/…/resourceGroups/…/providers/Microsoft.Insights/components/…`. |
| `Apps:N:Repository` | | The registered repository the app's code is in: investigations run there, and its Bugs are filed for it. |
| `Apps:N:RoleName` | (all) | Only telemetry with this `cloud_RoleName`, when several apps share one resource. |

**Sign-in** uses `DefaultAzureCredential`, never with a browser prompt. It tries a managed identity, then a service
principal (`AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_CLIENT_SECRET`), then the az CLI's login. The identity needs
**Reader** (or **Monitoring Reader**) on each component; there are no API keys. The Startup validation rejects a bad
list (duplicate names, an id that isn't a component, no repository).

## 2. Detecting (PR 1: 1a store, 1b pass and alerts)

`IncidentMonitorWorker` runs every `PollInterval` while the monitor is enabled, independent of the job scheduler. Each
pass runs `IncidentMonitor.PassAsync`. For each app, it reads the window `[cursor, now)`, where the first pass uses
`now − FirstWindow` and the window is capped at a day.

**The query** (`Infrastructure.Monitoring/TelemetryQueries.cs`, the only place KQL lives) is resource-centric on the
component, through `Azure.Monitor.Query.Logs` `LogsQueryClient.QueryResourceAsync`, using the classic
`exceptions` / `requests` tables:

| Source | Grouped by | Fingerprint | Title | Sample (latest occurrence) |
|---|---|---|---|---|
| `exceptions` | `problemId`, `operation_Name` | `ex|{problemId}|{operation}` | the exception type | outer message, `details[0].rawStack`, operation id |
| `requests` where `success == false` | `name`, `resultCode` | `req|{name}|{resultCode}` | `{name} → {code}` | url, operation id |

Each group also carries its count, distinct users, first and last seen, and `cloud_RoleName`. The query returns the
50 most frequent groups. Mapping a row to an `ErrorGroup` is a pure function with unit tests.

**Recording** (`agentd.incident_record`) is one row per `(app, fingerprint)`:
- A new group needs `MinCount` occurrences in the pass. Below that, nothing is stored.
- A known group always adds its count, widens its first and last seen, and keeps the newest sample.
- A **Resolved** incident that recurs **reopens** (status Open). An **Ignored** one stays ignored.
- Parallel callers on one fingerprint produce one row: every count is added and exactly one caller creates it.

**Alerting** happens for a created or reopened incident, within `MaxNewPerPass`:
- **Chat:** a 🚨 thread `🚨 {app}: {title}` opens on the default chat provider (the first enabled provider in
  `Messaging:DefaultProviders`, else the first enabled one), and the alert is posted there. A reopened incident posts
  🔁 in its existing thread.
- **Inbox:** the alert is always added there too (source `incident`). Without chat, the Inbox is where it goes.
- **Content:** the count over the window, users, operation, result code, first and last seen, the operation id, the top
  eight stack lines and a link to the component's Failures blade. All text passes through `SecretRedactor` first.

The app's cursor then moves to the window's end; it only ever moves forward. An app that can't be read (sign-in,
permissions, a query error) is logged and read again next pass from the same point, and the other apps go on. A failed
chat post is logged, and the Inbox still has the alert.

**Data:** `agentd.incidents` (migration `2026_11_08_0001`) holds status, total count, first and last seen, the sample
(jsonb), the 🚨 thread and the work item. `agentd.incident_cursors` holds each app's read position. Routines:
`incident_record`, `incident_get`, `incident_list`, `incident_set_thread`, `incident_cursor_get` and
`incident_cursor_set`.

## 3. Investigating and Fix it (PR 2)

### 3.1 Investigating (2a)

After the alert for a created or reopened incident, the monitor queues an investigation (`IncidentInvestigator`) in the
background, so the pass doesn't wait for it. At most two run at once, and one per incident.

1. **Status:** Open → Investigating, compare-and-set (`incident_set_status`). An incident that isn't Open (Ignored,
   Fixing, already investigating) isn't investigated.
2. **Telemetry, read by agentd:** the incident's three latest occurrences (`ITelemetrySource.OccurrencesAsync`,
   `TelemetryQueries.Occurrences`). They're matched by the fingerprint's parts: `problemId` and `operation_Name` for an
   exception, `name` and `resultCode` for a failed request. Each comes whole: the message (with the innermost one), the
   stack, the request's url and result code (or, for a request, the exception logged in the same operation), and the
   custom dimensions. If they can't be read, the incident's recorded sample is used.
   - **The agent never gets Azure access**, a token or daemon secrets. It can't query the telemetry itself.
3. **Turn:** a read-only agent turn (`ThreadTurnKind.Incident`, rules `IncidentRules`) in a detached checkout
   `incident-{id}` of `Apps:N:Repository`'s base branch. The prompt is the incident, its occurrences and where the code
   is, and **all of it passes through `SecretRedactor`**. The answer's shape is **Likely cause** (`path:line`),
   **Impact**, **Proposed fix** and **Confidence**. The turn is in the prompt log as source `incident` and in Restart
   now's list ("Incident N: the agent is investigating").
4. **Analysis:** redacted, kept on the incident with its session (`incident_set_analysis`: `analysis`, `session`,
   `analyzed_at`; Investigating → Open), and posted as **🔎 Investigation of incident #N** in the 🚨 thread and the Inbox,
   as a question with **🛠 Fix it** and **🙈 Ignore**.
5. **Failures** (an unregistered repository, an app no longer configured, a failed checkout, no answer, the usage limit)
   post a short line and put the incident back to Open, never leaving it in Investigating. An investigation a restart
   interrupts stays Investigating; an **investigate again** reply restarts it.

The `incident-{id}` checkout stays, because follow-up questions resume the session there. The worktree sweep leaves
folders it doesn't know.

**Data:** migration `2026_11_09_0001` adds `analysis`, `session` and `analyzed_at` to `agentd.incidents`, and `incident`
to the prompt log's sources and to the Inbox's (`ck_inbox_source`). That last one is a fix: until then the database
refused 1b's Inbox entries for incidents, and `InboxWriter` only logged it. Routines: `incident_set_status` (compare-and-set; parallel callers moving it from one status
have exactly one winner) and `incident_set_analysis`.

### 3.2 Fix it, Ignore and questions (2b)

Messages in the 🚨 thread (`InboundMessageHandler`, found by `incident_find_by_thread`) and Inbox replies
(`POST /api/inbox/incident/{id}/reply`) go to `IncidentService.HandleAsync`. One answer runs at a time per incident.

| Message | Who | What happens |
|---|---|---|
| **🛠 Fix it** · `fix` · `1` | Operators, Admins | A **Bug** in Azure DevOps: `[app] Title in operation`, in the repository's first match area path, tagged `ai-workflow` (`Jobs:Tag`), the repository's match tag (or `repo:<name>`) and `incident`, like an idea's Start, so agentd's poll picks it up and the normal job runs. Its description and **Repro Steps** are the redacted incident: the message, counts, stack, analysis and the portal link. `incident_set_work_item` keeps the first Bug only (parallel callers all get it back) and sets Fixing. A second Fix it names the filed Bug. |
| **🙈 Ignore** · `ignore` · `2` | Operators, Admins | Ignored: recurrences are counted without alerts (`incident_record` keeps it Ignored). |
| `reopen` · `watch again` | Operators, Admins | An Ignored or Resolved incident is Open again. |
| `investigate again` · `retry` | Operators, Admins | Investigating → Open, then a new investigation (for one a restart cut off, or a stale analysis). |
| anything else | anyone | A question: a read-only turn **resuming the investigation's session** in `incident-{id}`. It answers in the thread and the Inbox. Without an analysis yet, it investigates. |

Someone else gets "🔒 Only Operators and Admins can do that". Strangers on an open provider are Operators.

**Inbox:** an incident waits on you while it's **Open with an analysis and no Bug** (`inbox_waiting`). The Inbox shows
the question's buttons, and an incident has no page yet (PR 3).

**Resolved:** each monitor pass first checks Fixing incidents. When the Bug's latest job is **Done**, the incident is
Resolved: "✅ Incident #N is resolved: agentd's job for Bug #M is done (PR …)". If the error comes back, recording
reopens it and the alert says **🔁 Incident #N is back** in the same thread.

**Bugs:** the work item adapter writes a Bug's description to `Microsoft.VSTS.TCM.ReproSteps` too, because that is
what a Bug's form shows.

**Data:** migration `2026_11_10_0001` indexes `(provider, thread_id)`. Routines: `incident_set_work_item` (the first
Bug wins, with parallel callers) and `incident_find_by_thread`.

## 4. Web (PR 3, next)

- An **Incidents** page: the list by status and app, and each incident's alert, analysis and occurrences, with the
  same actions as the thread.
- **Settings → Monitoring**: the apps, with **Test connection**, which runs the query over the last hour.
