-- Inbound idempotency: each provider message is handled once.
-- inbound_try_record claims a message (false = already seen); the outcome is filled in afterwards.
-- If handling fails, inbound_forget releases the claim so a redelivery is handled again.
CREATE OR REPLACE FUNCTION agentd.inbound_try_record(p_provider text, p_external_message_id text, p_received_at timestamptz)
RETURNS boolean
LANGUAGE sql
AS $$
    WITH inserted AS (
        INSERT INTO agentd.inbound_messages (provider, external_message_id, outcome, received_at)
        VALUES (p_provider, p_external_message_id, 'processing', p_received_at)
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
    SELECT EXISTS (SELECT 1 FROM inserted);
$$;

CREATE OR REPLACE FUNCTION agentd.inbound_set_outcome(
    p_provider text, p_external_message_id text, p_outcome text, p_job_id bigint, p_user_id bigint)
RETURNS void
LANGUAGE sql
AS $$
    UPDATE agentd.inbound_messages
       SET outcome = p_outcome, job_id = p_job_id, user_id = p_user_id
     WHERE provider = p_provider AND external_message_id = p_external_message_id;
$$;

CREATE OR REPLACE FUNCTION agentd.inbound_forget(p_provider text, p_external_message_id text)
RETURNS void
LANGUAGE sql
AS $$
    DELETE FROM agentd.inbound_messages WHERE provider = p_provider AND external_message_id = p_external_message_id;
$$;
