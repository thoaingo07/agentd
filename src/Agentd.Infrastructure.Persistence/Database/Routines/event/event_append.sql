-- Appends one event and returns its sequence number.
CREATE OR REPLACE FUNCTION agentd.event_append(p_job_id bigint, p_type text, p_payload jsonb)
RETURNS bigint
LANGUAGE sql
AS $$
    INSERT INTO agentd.events (job_id, type, payload)
    VALUES (p_job_id, p_type, p_payload)
    RETURNING seq;
$$;
