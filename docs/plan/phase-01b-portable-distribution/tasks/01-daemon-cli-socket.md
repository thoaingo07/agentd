# T1b.1 — Daemon ↔ CLI over a Unix domain socket

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1b | T1b.5 | M | Host (CLI + daemon) |

## Goal
Let CLI verbs talk to the running daemon over `~/.agentd/run/agentd.sock`, so `status` and `run` see the
daemon's live state (activity, phase, usage) and go through its queue instead of straight to PostgreSQL.

## Files
- `src/Agentd.Host/Control/ControlSocket.cs`: create. Kestrel `ListenUnixSocket(run/agentd.sock)` on the daemon,
  mode 0600, removed on clean shutdown and replaced if stale at startup.
- `src/Agentd.Host/Control/ControlEndpoints.cs`: create. `GET /control/status`, `POST /control/run/{id}`, `GET /control/health`,
  mapped **only on the socket endpoint** (`RequireHost` on the socket's listen options), never on TCP.
- `src/Agentd.Host/Cli/DaemonClient.cs`: create. An `HttpClient` with a `SocketsHttpHandler.ConnectCallback` to the socket.
- `src/Agentd.Host/Cli/Commands/StatusCommand.cs`, `RunCommand.cs`: modify. Daemon first, falling back to the database.

## Implementation
1. The socket is the authority: file permissions (0600, owner only) are the access control. No token.
2. `status` asks the daemon; if the socket is missing or refuses, it prints `(daemon not running; from the database)`
   and uses the current Application queries.
3. `run` through the daemon wakes the scheduler at once (no wait for the next poll).
4. Windows: named pipe later; the CLI falls back to the database there.

## Tests
- The socket is created 0600 and removed on shutdown; a stale socket file doesn't block startup.
- `/control/*` is unreachable over TCP (404) and reachable over the socket.
- `status` / `run` work with and without the daemon (integration test with a real socket in a temp dir).

## Done when
- [ ] `agentd status` shows live phase/activity when the daemon runs, and falls back cleanly when it doesn't.
- [ ] `/control/*` is only on the socket (test).
- [ ] `docs/architect/deployment.md` §1 updated if anything differs.
