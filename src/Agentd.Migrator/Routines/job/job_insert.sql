-- Inserts a new job and its domain events atomically. Raises AG409 if the work item already has an active job.
-- p_events: [{"type": "...", "payload": {...}}, ...]
DROP FUNCTION IF EXISTS agentd.job_create(int);

CREATE OR REPLACE FUNCTION agentd.job_insert(
    p_work_item_id int, p_repo text, p_title text, p_state text, p_attempt int,
    p_created_at timestamptz, p_events jsonb)
RETURNS TABLE (id bigint, version bigint)
LANGUAGE plpgsql
AS $$
DECLARE
    v_id bigint;
BEGIN
    INSERT INTO agentd.jobs AS j (work_item_id, repo, title, state, attempt, created_at, updated_at)
    VALUES (p_work_item_id, p_repo, p_title, p_state, p_attempt, p_created_at, p_created_at)
    RETURNING j.id INTO v_id;

    INSERT INTO agentd.events (job_id, type, payload)
    SELECT v_id, e ->> 'type', e -> 'payload' FROM jsonb_array_elements(coalesce(p_events, '[]'::jsonb)) AS e;

    RETURN QUERY SELECT v_id, 1::bigint;
EXCEPTION
    WHEN unique_violation THEN
        RAISE EXCEPTION 'work item % already has an active job', p_work_item_id USING ERRCODE = 'AG409';
END
$$;
