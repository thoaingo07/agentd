# T3.4 — SignalR events hub with replay

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | T3.1 | M | Agentd.Bff |

## Goal
Stream live events to the browser over SignalR at `/hubs/events`. A client subscribes to
`all` (dashboard summary events) or to one job (the full stream), passing the last `seq` it has.
The hub **first replays** everything after that `seq` from the store, then streams live events, so
reconnecting never loses or duplicates an event. The hub is read-only and only accepts same-origin
connections.

## Files
- `src/Agentd.Bff/Hubs/EventsHub.cs` — create.
- `src/Agentd.Bff/Hubs/HubSubscriptionManager.cs` — create: per-connection subscriptions and pump tasks.
- `src/Agentd.Bff/Security/OriginAllowlistMiddleware.cs` — create (shared with T3.6).
- `src/Agentd.Bff/BffModule.cs` — modify: `AddSignalR()` and `MapHub<EventsHub>("/hubs/events").RequireAuthorization()`.

## Implementation
1. **Hub contract** (client → server):
   - `Subscribe(string stream, long afterSeq)`, where `stream` is `"all"` or a job ID as a string;
   - `Unsubscribe(string stream)`.

   Server → client: `event(AgentEventDto)`. There are **no** methods that change state; commands go
   through REST.
2. **Subscribe flow**, implemented in `HubSubscriptionManager`:
   1. Validate the stream, and that the job exists for job streams.
   2. Start a pump task for `(connectionId, stream)`.
   3. The pump **subscribes to `IEventPublisher` first**, buffering live events.
   4. It then **replays** `IEventStore.ReadAfterAsync(stream, afterSeq, 500)` in pages until caught up.
   5. It drains the buffer, skipping any `seq` ≤ the last replayed `seq`.
   6. It streams live events.

   Subscribing before replaying closes the race where an event lands between the two.
3. A subscriber whose channel dropped events (T3.1 `DropOldest`) re-enters replay from its last sent
   `seq`.
4. `OnDisconnectedAsync` cancels all of that connection's pumps. Limit: at most 20 subscriptions per
   connection.
5. **Payload trimming:**
   - the `all` stream sends only summary types;
   - job streams send full events, but `tool.result` payloads over 64 KB are truncated with
     `truncated: true`, and the UI fetches the full event on demand via REST;
   - verify the SignalR message size limit (`MaximumReceiveMessageSize` applies to client → server
     only, which is fine).
6. **Origin check:**
   - reject `/hubs/*` requests (negotiate and the WebSocket upgrade) whose `Origin` header is not the
     app's own origin → 403;
   - the allowed origins come from `Web:PublicOrigin`, or are derived from the bound URL in
     loopback mode;
   - requests without an `Origin` header are rejected on `/hubs`.
7. **Transport:** WebSockets preferred, with the default fallbacks allowed. Keep the default JSON
   protocol, because MessagePack would add a client dependency.

## Tests
- `Bff.Tests` with `Microsoft.AspNetCore.SignalR.Client` against `WebApplicationFactory`:
  - subscribe with `afterSeq = 0` to a job with 50 events → receives exactly 50, in order;
  - then 5 live events arrive → the client has seq 1–55 with no duplicates;
  - disconnect and resubscribe with `afterSeq = 30` → receives 31–55 once;
  - the race test: a publisher writes continuously while the client subscribes, and the received
    `seq` list is contiguous;
  - `Origin: https://evil.example` → the negotiate request gets 403;
  - calling an unknown hub method fails; there is no server method that mutates state.

## Done when
- [x] Replay-then-live with no gaps or duplicates, proven by the race test.
- [x] The `all` stream carries only summary events, and job streams carry full ones.
- [x] A cross-origin connection is refused.
- [x] The hub requires authentication (the local pseudo-user in Mode None).

## As built
- **Events commit in `seq` order** (a prerequisite that T3.1 missed). Several transactions write
  events at once. A transaction holding seq 10 could commit after one holding 11, and then every
  "after seq N" reader (hub replay, UI reconnect) would skip 10 forever.
  - Fix: the `events_serialize` trigger (`BEFORE INSERT … FOR EACH STATEMENT` on `agentd.events`)
    takes `pg_advisory_xact_lock`, so seq values are drawn and committed one writer at a time.
  - `EventStreamTests.Concurrent_writers_commit_events_in_seq_order` fails without the trigger and
    passes with it.
- **`ILiveEvents` changes:**
  - `Subscribe` registers the subscriber **when called**, not on first enumeration. That's what
    makes "subscribe, then replay" airtight.
  - A full buffer no longer drops the oldest events silently. The stream ends with
    `LiveEventsOverflowException` after the buffered events, and the pump re-subscribes and replays
    from its last sent `seq`.
- **Files and contract:**
  - `Hubs/EventsHub.cs` (`Subscribe(stream, afterSeq)`, `Unsubscribe(stream)`), plus
    `Hubs/EventStreams.cs` (the pumps). The file is named `EventStreams`, not
    `HubSubscriptionManager`.
  - Server → client is `event(stream, EventVm)`. The stream name is included so the client can keep
    `lastSeq` per stream.
- **Payloads over 64 KB** are streamed as `{ "truncated": true, "bytes": n }`. The full event comes
  from the new `GET /api/jobs/{id}/events/{seq}` (404 if the seq belongs to another job).
- **Origin guard:** `Http/HubOriginGuard.cs` (`UseBffHubOriginGuard`, called by the Host).
  - `/hubs/*` needs `Origin` = the request's own `scheme://host`, or `Web:PublicOrigin`. Missing or
    foreign → 403.
  - The planned shared `Security/OriginAllowlistMiddleware.cs` is left to T3.6, if CSP needs it.
- **Tests:** `Bff.Tests/EventsHubTests` uses the SignalR .NET client over TestServer long polling,
  with an in-memory store that commits in seq order and the real `EventHub`:
  - replay, then live, giving 1–55;
  - resume after 30;
  - subscribing while a writer runs (contiguous);
  - overflow → resume;
  - the `all` stream is summary-only, and big payloads are trimmed;
  - read-only (an unknown method fails), with stream validation;
  - foreign or missing `Origin` → 403;
  - disconnecting stops the pumps.

  These run in-process with fakes, not WebApplicationFactory + PostgreSQL. The commit-order
  guarantee is tested against PostgreSQL in Infrastructure.
