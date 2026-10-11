# System log

> Decided 2026-10-10. The daemon's own log, readable in the Web UI (Settings → **System log**, Admins only).

Until now agentd logged to the console only (journald when installed as a service: `agentd daemon logs`), plus
OpenTelemetry when configured. Neither reaches a Web UI hosted apart from the daemon, so the log is also kept in the
database, like artifacts.

## What's kept

- **Capture:** `SystemLogProvider` (Host, `daemon run` only) keeps agentd's own categories (`Agentd.*`) from
  `Agentd:SystemLog:MinimumLevel` (default Information) and every other library's from Warning. The console, journald
  and OpenTelemetry keep logging as before.
- **Never itself:** the writer's own entries and Npgsql's are skipped, so a database problem while storing the log
  can't feed the log.
- **Redacted:** the message and the exception go through `SecretRedactor` (like events) before they're queued.
- **Linked:** a `{JobId}` in the message's values becomes the entry's job (the page links to it).
- **Never blocking:** `SystemLogWriter` takes entries through a bounded queue (10,000) and stores them in batches
  (up to 500, about every second) with `system_log_add` (parallel arrays, one round trip). Entries that can't be kept
  (queue full, database unavailable, e.g. before setup) are counted and reported by one warning once storing works.
- **Retention:** `Agentd:SystemLog:RetentionDays` (default 14; 0 = forever). The hourly sweep calls
  `system_log_delete_before`. `Agentd:SystemLog:Enabled = false` keeps nothing in the database.

## Table and routines

`agentd.system_logs` (at, level as `LogLevel`, category, message, exception, job_id), with `system_log_add`,
`system_log_list` (newest first, keyset by id; minimum level, category and text filters, case-insensitive) and
`system_log_delete_before`.

## API and page

- `GET /api/system-log?before=&level=&category=&q=&limit=` (Admins; at most 200 a page, `hasMore` for older ones).
- Settings → **System log**: level, source and search filters; newest first, re-read every 5 s while you're at the
  top (not while reading older pages); exceptions fold open; job links.
- Admins only: unlike artifacts, the log shows internals (paths, ids, hosts).
