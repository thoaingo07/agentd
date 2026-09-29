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
- [ ] Replay-then-live with no gaps or duplicates, proven by the race test.
- [ ] The `all` stream carries only summary events, and job streams carry full ones.
- [ ] A cross-origin connection is refused.
- [ ] The hub requires authentication (the local pseudo-user in Mode None).
