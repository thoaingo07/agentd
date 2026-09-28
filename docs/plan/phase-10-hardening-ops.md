# Phase 10 — Hardening & operations

**Goal:** make agentd dependable to run every day. That means kit upgrades, data retention and
backups, a production deploy, enforced Trusted Types, the MAF Harness benchmark, and an operator
runbook.

Design refs: [ai-sdlc-kit.md §2.3](../architect/ai-sdlc-kit.md#23-upgrade) ·
[orchestration-maf.md §6, §8](../architect/orchestration-maf.md#6-optional-runner-maf-harness-agent-experimental) ·
[Web Security §3.4](../security/README.md#34-trusted-types)

---

## Scope

**In**

- **Kit upgrade:** `agentd kit upgrade` + a UI button. A 3-way merge (`git merge-file`) against the
  baseline hashes, conflict markers left in the PR, a changelog, and the `checklists/` →
  `reviewers/general.md` migration.
- **Retention:**
  - `RetentionWorker` drops old event partitions, trims old checkpoints (keeping the latest *N* and
    the final one per job), and cleans up transcripts and worktrees;
  - retention is configurable per data type.
- **Backups:** a PostgreSQL backup and restore procedure (`pg_dump` schedule + a tested restore),
  with Data Protection keys included.
- **Deploy:**
  - `deploy/agentd.service` (systemd; `Restart=on-failure`, hardening options such as
    `ProtectSystem`, `NoNewPrivileges` and `PrivateTmp` where they are compatible);
  - reverse-proxy examples;
  - an upgrade procedure (migrations, then restart, then workflow resume).
- **Security hardening:**
  - **enforce Trusted Types** once the report-only data is clean;
  - optional MFA (`amr`) for Admin actions;
  - dependency and vulnerability scanning in CI;
  - secret scanning.
- **The MAF Harness Agent runner** (experimental) behind `IAgentRunner`, plus a **benchmark
  harness**: re-run a fixed set of past work items per repo with both runners and compare pass
  rate, fix loops, turns and cost.
- **Observability:** OpenTelemetry exporter to your backend (e.g. OTLP → Grafana or Azure
  Monitor), key dashboards (jobs per state, phase durations, cost per profile, breaker trips), and
  alerts in chat.
- **Runbook** in `docs/ops/`: install, configure, onboard a repo, rotate secrets, troubleshoot a
  stuck job, restore from backup.

**Out:** multi-host scheduling, and a browser terminal (both non-goals for v1).

---

## Tasks

1. The kit upgrade engine + tests (unchanged, customized, and conflicting files; added and removed
   files).
2. The retention worker + configuration.
3. The backup script + a restore drill in staging.
4. The systemd unit + hardening; reverse-proxy examples; the upgrade procedure.
5. Trusted Types enforcement (after a report-only period with no violations); CI scanners.
6. `HarnessAgentRunner` (flagged) + the benchmark runner + a report page.
7. The OTel exporter configuration, dashboards and alert rules.
8. The operator runbook.

## Exit criteria (the demo)

- `kit upgrade` on a repo with customized phase files: untouched files are replaced, customized
  ones are merged or flagged, and the upgrade PR has a readable changelog.
- A restore from last night's backup into a fresh PostgreSQL brings back jobs, sessions and
  checkpoints. Waiting jobs resume, and users stay signed in.
- The systemd service survives a reboot. A rolling upgrade (migrate + restart) resumes all
  in-flight jobs.
- Trusted Types are enforced with zero violations in the Playwright suite.
- The benchmark report compares the Claude Code and MAF Harness runners on at least 10 past work
  items for one repo.

## Risks / open questions for review

- **Observability backend:** which one? (Grafana stack, Azure Monitor, or something else.)
- **Where agentd runs in production:** this machine, a dedicated VM, or a container? (systemd is
  assumed.)
- **Benchmark set:** which past work items are representative?
