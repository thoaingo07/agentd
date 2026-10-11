-- The daemon's own log (Settings → System log).

-- One batch from the log writer, as parallel arrays (one round trip). Returns how many rows were added.
CREATE OR REPLACE FUNCTION agentd.system_log_add(p_at timestamptz[], p_level smallint[], p_category text[], p_message text[], p_exception text[], p_job_id bigint[])
RETURNS int
LANGUAGE sql
AS $$
    WITH added AS (
        INSERT INTO agentd.system_logs (at, level, category, message, exception, job_id)
        SELECT * FROM unnest(p_at, p_level, p_category, p_message, p_exception, p_job_id)
        RETURNING 1
    )
    SELECT count(*)::int FROM added
$$;

-- Newest first, older than p_before_id (null: the newest), at p_min_level or above; p_category matches any part of the
-- category, p_search any part of the message or exception (both case-insensitive, optional).
CREATE OR REPLACE FUNCTION agentd.system_log_list(p_before_id bigint, p_min_level smallint, p_category text, p_search text, p_limit int)
RETURNS SETOF agentd.system_logs
LANGUAGE sql STABLE
AS $$
    SELECT * FROM agentd.system_logs AS l
     WHERE (p_before_id IS NULL OR l.id < p_before_id)
       AND l.level >= p_min_level
       AND (p_category IS NULL OR l.category ILIKE '%' || p_category || '%')
       AND (p_search IS NULL OR l.message ILIKE '%' || p_search || '%' OR l.exception ILIKE '%' || p_search || '%')
     ORDER BY l.id DESC
     LIMIT p_limit
$$;

-- Retention: deletes entries logged before p_before; returns how many.
CREATE OR REPLACE FUNCTION agentd.system_log_delete_before(p_before timestamptz)
RETURNS int
LANGUAGE sql
AS $$
    WITH deleted AS (DELETE FROM agentd.system_logs WHERE at < p_before RETURNING 1)
    SELECT count(*)::int FROM deleted
$$;
