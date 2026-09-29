-- Creates a queued job and returns its identity, state and version.
CREATE OR REPLACE FUNCTION agentd.job_create(p_work_item_id int)
RETURNS TABLE (id bigint, state text, version bigint)
LANGUAGE sql
AS $$
    INSERT INTO agentd.jobs (work_item_id, state)
    VALUES (p_work_item_id, 'Queued')
    RETURNING jobs.id, jobs.state, jobs.version;
$$;
