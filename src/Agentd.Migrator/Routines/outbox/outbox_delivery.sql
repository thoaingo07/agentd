-- Outbox delivery (the dispatcher). Rows go pending → sending → sent | failed | dead.
-- While a row is 'sending', next_attempt_at holds the claim time (for the stale sweeper).

-- Claims due rows: only the oldest unsent row of each conversation (in-order delivery, at most one
-- in flight per conversation), only for the given providers (open circuits are left out).
CREATE OR REPLACE FUNCTION agentd.outbox_claim(p_limit int, p_providers text[], p_now timestamptz)
RETURNS TABLE (id bigint, job_id bigint, conversation_id bigint, provider text, kind text, payload jsonb,
               attempts int, external_conversation_id text, external_space_id text, status_message_id text)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    WITH due AS (
        SELECT o.id FROM agentd.outbound_messages AS o
         WHERE o.status = 'pending' AND o.next_attempt_at <= p_now AND o.provider = ANY (p_providers)
           AND NOT EXISTS (SELECT 1 FROM agentd.outbound_messages AS earlier
                            WHERE earlier.conversation_id = o.conversation_id AND earlier.id < o.id
                              AND earlier.status IN ('pending', 'sending'))
         ORDER BY o.id
         LIMIT p_limit
           FOR UPDATE SKIP LOCKED
    ), claimed AS (
        UPDATE agentd.outbound_messages AS o SET status = 'sending', next_attempt_at = p_now
          FROM due WHERE o.id = due.id
        RETURNING o.*
    )
    SELECT c.id, c.job_id, c.conversation_id, c.provider, c.kind, c.payload, c.attempts,
           conv.external_conversation_id, conv.external_space_id, conv.status_message_id
      FROM claimed AS c JOIN agentd.conversations AS conv ON conv.id = c.conversation_id
     ORDER BY c.id;
END
$$;

-- Delivered. p_status_message: this send created the conversation's live status message.
CREATE OR REPLACE FUNCTION agentd.outbox_mark_sent(p_id bigint, p_external_message_id text, p_status_message boolean)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_conversation bigint;
BEGIN
    UPDATE agentd.outbound_messages SET status = 'sent', external_message_id = p_external_message_id, last_error = NULL
     WHERE id = p_id RETURNING conversation_id INTO v_conversation;
    IF p_status_message THEN
        UPDATE agentd.conversations SET status_message_id = p_external_message_id WHERE id = v_conversation;
    END IF;
END
$$;

-- A transient failure: retry at p_next_attempt_at, or give up ('dead') after p_max_attempts. Returns the new status.
CREATE OR REPLACE FUNCTION agentd.outbox_mark_retry(p_id bigint, p_error text, p_next_attempt_at timestamptz, p_max_attempts int)
RETURNS text
LANGUAGE plpgsql
AS $$
DECLARE
    v_row agentd.outbound_messages;
BEGIN
    UPDATE agentd.outbound_messages AS o
       SET attempts = o.attempts + 1, last_error = p_error, next_attempt_at = p_next_attempt_at,
           status = CASE WHEN o.attempts + 1 >= p_max_attempts THEN 'dead' ELSE 'pending' END
     WHERE o.id = p_id RETURNING o.* INTO v_row;
    IF v_row.status = 'dead' THEN
        INSERT INTO agentd.events (job_id, type, payload)
        VALUES (v_row.job_id, 'MessagingDeliveryDead',
                jsonb_build_object('provider', v_row.provider, 'attempts', v_row.attempts, 'error', p_error));
    END IF;
    RETURN v_row.status;
END
$$;

-- A permanent failure (bad request, thread deleted, bot removed): no retry.
CREATE OR REPLACE FUNCTION agentd.outbox_mark_failed(p_id bigint, p_error text)
RETURNS void
LANGUAGE sql
AS $$
    WITH failed AS (
        UPDATE agentd.outbound_messages SET status = 'failed', last_error = p_error WHERE id = p_id
        RETURNING job_id, provider
    )
    INSERT INTO agentd.events (job_id, type, payload)
    SELECT job_id, 'MessagingDeliveryFailed', jsonb_build_object('provider', provider, 'error', p_error) FROM failed;
$$;

-- Returns rows stuck in 'sending' since before p_claimed_before (a crashed or stopped dispatcher) to 'pending'.
CREATE OR REPLACE FUNCTION agentd.outbox_release_stale(p_claimed_before timestamptz)
RETURNS int
LANGUAGE sql
AS $$
    WITH released AS (
        UPDATE agentd.outbound_messages SET status = 'pending'
         WHERE status = 'sending' AND next_attempt_at < p_claimed_before
        RETURNING 1
    )
    SELECT count(*)::int FROM released;
$$;
