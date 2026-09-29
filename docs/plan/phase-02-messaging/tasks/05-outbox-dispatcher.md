# T2.5 — Outbox dispatcher, retries and provider health

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 2 | T2.4 | M | Agentd.Host, Agentd.Infrastructure.Persistence |

## Goal
Deliver outbox rows reliably, with per-provider backoff and rate limits. A provider outage must
**never fail a job**: messages wait and flush later, and `/healthz` shows the provider as degraded.

## Files
- `src/Agentd.Host/Workers/MessagingDispatcherWorker.cs` — create.
- `src/Agentd.Infrastructure.Persistence/Messaging/OutboxQueries.cs` — create: claim and update rows.
- `src/Agentd.Host/Health/MessagingProviderHealthCheck.cs` — create.
- `src/Agentd.Application/Messaging/MessagingRegistry.cs` — create: implements `IMessagingProviderRegistry`.

## Implementation
1. **Claim loop** (every second, or woken by a `NOTIFY outbox` after insert):
   ```sql
   SELECT * FROM outbound_messages
   WHERE status = 'pending' AND next_attempt_at <= now()
   ORDER BY id
   FOR UPDATE SKIP LOCKED
   LIMIT 50;
   ```
   Mark the claimed rows `sending` in the same transaction, then send outside it.
2. **Ordering:** messages to the **same conversation** are delivered in `id` order. At most one row
   per conversation is in flight at a time, enforced by grouping the claimed rows.
3. **Send:**
   - chunk (T2.4);
   - render and send through the provider;
   - store `external_message_id` for the first chunk (and the status message ID when it's new);
   - `status = sent`.
4. **Failure handling:**
   - transient failures (5xx, timeouts, 429) → `attempts++`, with `next_attempt_at = now +
     min(2^attempts s, 5 min)` plus jitter. On a 429, the provider's `retry_after` wins.
   - permanent failures (400 bad request, thread deleted, bot kicked) → `failed`, with `last_error`,
     and a `messaging.delivery_failed` job event;
   - after 20 attempts → `dead`, and a warning event.
5. **Conversation open retry:** if a job has a target provider but no conversation row (T2.4 step
   1 failed), the dispatcher retries the open before sending that job's pending rows.
6. **Health:** `CheckHealthAsync` per provider every 30 s, cached. `/healthz` reports `Degraded`,
   not `Unhealthy`, when a provider is down, so the host stays in rotation. A provider's circuit
   is open after 5 consecutive failures, which pauses its sends for 60 s.
7. **Shutdown:** stop claiming, finish in-flight sends (with a 10 s grace period), and put
   unfinished rows back to `pending`.

## Tests
- Integration (PostgreSQL + a fake provider):
  - ordering per conversation;
  - parallel delivery across conversations;
  - 429 honours `retry_after`;
  - permanent error → `failed`;
  - crash while `sending` → a stale-`sending` sweeper (older than 2 min) returns rows to `pending`.
- Outage simulation: the fake provider throws for 2 minutes → the job completes anyway → all
  messages arrive afterwards, in order.
- Health check reports Degraded during the outage.

## Done when
- [ ] Killing the network to Discord doesn't fail or stall a job, and messages flush on recovery.
- [ ] No duplicate delivery after a dispatcher crash in a normal test run. At-least-once delivery
  is acceptable; the dedupe window is documented.
- [ ] `/healthz` shows the per-provider status.
