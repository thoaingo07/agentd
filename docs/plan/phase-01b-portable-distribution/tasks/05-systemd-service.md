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
- [ ] On Ubuntu: `agentd daemon install && agentd daemon start` → running after a reboot (manual, on the VPS demo).

## As built (2026-10-06)
- **Verbs:** `agentd daemon install | uninstall | start | stop | restart | status | logs [-f] [-n N]`, over
  `systemctl --user` and `journalctl --user -u agentd.service`.
  - `status` treats systemctl's exit 3 (stopped) as an answer, not an error.
  - `uninstall` keeps `~/.agentd`.
- **The unit** goes in `$XDG_CONFIG_HOME/systemd/user/agentd.service` (or `~/.config/systemd/user`):
  - `ExecStart` is this executable plus `daemon run` (`dotnet agentd.dll daemon run` in a development build);
  - `WorkingDirectory=%h`, `Restart=on-failure` (5 s), `KillSignal=SIGTERM` with `TimeoutStopSec=60`;
  - `Environment=AGENTD_HOME=…` only when set;
  - **no secrets**;
  - every value is systemd-quoted, with `%` doubled.
- **Lingering:** `install` checks `loginctl show-user $USER --property=Linger` and prints the `sudo loginctl
  enable-linger` hint when it's off.
- **Off Linux, or without a systemd user manager:** a clear message pointing to `agentd daemon run` or Docker.
- **Tests** use a fake process runner and a temporary `XDG_CONFIG_HOME`. Nothing was installed on the development
  machine; the live check is part of the VPS demo.
- `restart` was added (the secrets docs say "restart the daemon").

