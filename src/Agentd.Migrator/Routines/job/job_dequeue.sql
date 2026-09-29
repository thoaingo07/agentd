-- Atomically claims the oldest runnable queued job (not_before passed) and moves it to Preparing.
-- FOR UPDATE SKIP LOCKED: concurrent callers never get the same job. Records a JobPreparing event.
CREATE OR REPLACE FUNCTION agentd.job_dequeue(p_worker text, p_now timestamptz)
RETURNS SETOF agentd.jobs
LANGUAGE plpgsql
AS $$
DECLARE
    v_id bigint;
BEGIN
    SELECT j.id INTO v_id
      FROM agentd.jobs AS j
     WHERE j.state = 'Queued' AND (j.not_before IS NULL OR j.not_before <= p_now)
     ORDER BY j.created_at, j.id
     FOR UPDATE SKIP LOCKED
     LIMIT 1;

    IF v_id IS NULL THEN
        RETURN;
    END IF;

    INSERT INTO agentd.events (job_id, type, payload)
    VALUES (v_id, 'JobPreparing', jsonb_build_object('OccurredAt', p_now, 'Worker', p_worker));

    RETURN QUERY
    UPDATE agentd.jobs AS j
       SET state = 'Preparing', claimed_by = p_worker, updated_at = p_now, version = j.version + 1
     WHERE j.id = v_id
    RETURNING j.*;
END
$$;
