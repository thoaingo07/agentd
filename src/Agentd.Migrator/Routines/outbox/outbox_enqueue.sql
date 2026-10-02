-- Writes outbox rows: one per message per matching open conversation of the job.
-- p_messages: [{ "kind": "Info", "payload": {...}, "only": ["discord"] | null, "except": [...] | null, "replace": false }]
-- "replace" (progress): drops the conversation's pending replaceable rows of the same kind first, so
-- only the newest update waits. Returns the number of rows written. Called alone, or batched with
-- job_save so the messages commit with the state change.
CREATE OR REPLACE FUNCTION agentd.outbox_enqueue(p_job_id bigint, p_messages jsonb)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    m jsonb;
    c record;
    v_count int := 0;
BEGIN
    FOR m IN SELECT * FROM jsonb_array_elements(coalesce(p_messages, '[]'::jsonb)) LOOP
        FOR c IN
            SELECT conv.id, conv.provider FROM agentd.conversations AS conv
             WHERE conv.job_id = p_job_id AND conv.closed_at IS NULL
               AND (jsonb_typeof(m -> 'only') IS DISTINCT FROM 'array'
                    OR conv.provider IN (SELECT jsonb_array_elements_text(m -> 'only')))
               AND (jsonb_typeof(m -> 'except') IS DISTINCT FROM 'array'
                    OR conv.provider NOT IN (SELECT jsonb_array_elements_text(m -> 'except')))
        LOOP
            IF coalesce((m ->> 'replace')::boolean, false) THEN
                DELETE FROM agentd.outbound_messages AS o
                 WHERE o.conversation_id = c.id AND o.status = 'pending' AND o.kind = m ->> 'kind'
                   AND coalesce((o.payload ->> 'replaceStatusMessage')::boolean, false);
            END IF;

            INSERT INTO agentd.outbound_messages (job_id, conversation_id, provider, kind, payload)
            VALUES (p_job_id, c.id, c.provider, m ->> 'kind', m -> 'payload');
            v_count := v_count + 1;
        END LOOP;
    END LOOP;

    RETURN v_count;
END
$$;
