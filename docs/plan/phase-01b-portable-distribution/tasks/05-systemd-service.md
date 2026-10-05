# T1b.5 — systemd user service

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1b | T1b.6 | S | Host (CLI) |

## Goal
`agentd daemon install|uninstall|start|stop|status|logs` manage a **systemd user service**, so the daemon
starts at boot and restarts on failure, without root.

## Files
- `src/Agentd.Host/Cli/Commands/DaemonCommand.cs`: modify. The new verbs.
- `src/Agentd.Host/Cli/SystemdUnit.cs`: create. Renders `~/.config/systemd/user/agentd.service`.

## Implementation
1. Unit: `ExecStart=<path to agentd> daemon run`, `Restart=on-failure`, `Environment=AGENTD_HOME=…` when set,
   `WorkingDirectory=%h`, no secrets in the unit.
2. `install` writes the unit, runs `systemctl --user daemon-reload` and `enable`, and prints
   `loginctl enable-linger $USER` (needed to run without a login session) if linger is off.
3. `start|stop|status` call `systemctl --user …`; `logs` runs `journalctl --user -u agentd -f` (with `-n`).
4. Not Linux, or no systemd: a clear message (macOS launchd later).

## Tests
- The rendered unit for a given binary path and home (snapshot).
- Verbs call the expected `systemctl` arguments (fake process runner).

## Done when
- [ ] On Ubuntu: `agentd daemon install && agentd daemon start` → running after a reboot (manual).
