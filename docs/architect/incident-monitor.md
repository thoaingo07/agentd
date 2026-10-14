# agentd — Incident monitor (Application Insights)

> Decided 2026-10-14. It replaces the PR Monitor's place: agentd watches **running apps**, not PRs.
> Built in three PRs. **PR 1 (this one)** detects incidents and alerts on them. **PR 2** investigates and offers
> **Fix it**. **PR 3** adds the web pages. The PR 2 and PR 3 sections below are marked *next*.

agentd reads each configured app's **Application Insights** telemetry for exceptions and failed requests. It groups
them into **incidents**, one per bug however often it happens, and alerts in a 🚨 thread on the chat channel, or in the
Inbox when there is no chat. *Next:* an agent investigates each incident against the app's repository, and on
**Fix it** agentd files an Azure DevOps Bug for the normal job pipeline: plan, implement, verify, PR, review loop.

Decisions made with the user:

| Question | Decision |
|---|---|
| What triggers an incident | agentd **queries the telemetry itself** (exceptions, failed requests). It does not use Azure Monitor alert rules, so the only Azure setup is read access, and it works behind Tailscale with no inbound webhook. |
| How a fix reaches a PR | *(next)* agentd **creates a work item** (a Bug with the analysis, tagged for agentd). The normal job runs, which keeps it traceable on the board. |
| When fixing starts | *(next)* **A person asks first**: the alert and the investigation are automatic, and **Fix it** (a button or a reply) files the work item. |

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
| `Apps:N:Repository` | | The registered repository the app's code is in. *Next:* investigations and fixes run there. |
| `Apps:N:RoleName` | (all) | Only telemetry with this `cloud_RoleName`, when several apps share one resource. |

**Sign-in** uses `DefaultAzureCredential`, never with a browser prompt. It tries a managed identity, then a service
principal (`AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_CLIENT_SECRET`), then the az CLI's login. The identity needs
**Reader** (or **Monitoring Reader**) on each component; there are no API keys. The Startup validation rejects a bad
list (duplicate names, an id that isn't a component, no repository).

## 2. Detecting (PR 1)

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
(jsonb), the 🚨 thread and *next* the work item. `agentd.incident_cursors` holds each app's read position. Routines:
`incident_record`, `incident_get`, `incident_list`, `incident_set_thread`, `incident_cursor_get` and
`incident_cursor_set`.

## 3. Investigating and Fix it (PR 2, next)

- **Investigate:** on a new or reopened incident, a read-only agent turn (`ThreadTurnKind.Incident`) runs in a checkout
  of `Apps:N:Repository`'s default branch. It gets the sample and stack, and a tool for further read-only KQL on that
  component. It posts a root-cause analysis in the 🚨 thread. Status: Investigating, then Open.
- **Ask first:** the thread, and the Inbox (now *waiting*), offer **Fix it**, **Ignore** and **Resolved**. Replies in
  the thread go to the agent, like a chat.
- **Fix it** files an Azure DevOps **Bug** in the repository's area path, tagged for agentd, with the analysis, the
  sample and a link back. The normal job picks it up. Status is Fixing, and it becomes Resolved when the job's PR
  merges. A recurrence after that reopens the incident.

## 4. Web (PR 3, next)

- An **Incidents** page: the list by status and app, and each incident's alert, analysis and occurrences, with the
  same actions as the thread.
- **Settings → Monitoring**: the apps, with **Test connection**, which runs the query over the last hour.
