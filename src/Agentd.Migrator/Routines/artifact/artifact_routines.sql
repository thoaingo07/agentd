-- Artifacts (HTML pages agents publish).

-- Publishes a page: a new one gets p_name; the same job or chat publishing the same title again replaces the HTML and
-- keeps the first name (its link), and brings an expired page back. Parallel publishes of one title make one row. A job's page is announced on its
-- event stream (name and title only).
CREATE OR REPLACE FUNCTION agentd.artifact_publish(p_name text, p_title text, p_html text, p_job_id bigint, p_chat_id bigint, p_author text)
RETURNS TABLE (name text, created boolean)
LANGUAGE plpgsql
AS $$
DECLARE
    v_name    text;
    v_created boolean;
BEGIN
    INSERT INTO agentd.artifacts AS a (name, title, html, job_id, chat_id, author)
    VALUES (p_name, p_title, p_html, p_job_id, p_chat_id, p_author)
    ON CONFLICT (job_id, chat_id, title) DO UPDATE SET html = excluded.html, author = excluded.author, updated_at = now(), expired_at = NULL
    RETURNING a.name, (a.xmax = 0) INTO v_name, v_created;

    IF p_job_id IS NOT NULL THEN
        INSERT INTO agentd.events (job_id, type, payload)
        VALUES (p_job_id, 'artifact.published', jsonb_build_object('name', v_name, 'title', p_title, 'created', v_created));
    END IF;

    RETURN QUERY SELECT v_name, v_created;
END
$$;

CREATE OR REPLACE FUNCTION agentd.artifact_get(p_name text)
RETURNS SETOF agentd.artifacts
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.artifacts WHERE artifacts.name = p_name $$;

-- The newest live pages first, without their HTML (the Web UI's list); expired ones aren't listed.
CREATE OR REPLACE FUNCTION agentd.artifact_list(p_limit int)
RETURNS TABLE (name text, title text, job_id bigint, chat_id bigint, author text, created_at timestamptz, updated_at timestamptz)
LANGUAGE sql STABLE
AS $$
    SELECT a.name, a.title, a.job_id, a.chat_id, a.author, a.created_at, a.updated_at
      FROM agentd.artifacts AS a
     WHERE a.html IS NOT NULL
     ORDER BY a.updated_at DESC, a.id DESC
     LIMIT p_limit
$$;

-- Expires pages last updated before p_before: drops the HTML and keeps a tombstone (the link answers "expired").
-- Returns how many expired now; parallel callers never count a page twice.
CREATE OR REPLACE FUNCTION agentd.artifact_expire(p_before timestamptz)
RETURNS int
LANGUAGE sql
AS $$
    WITH expired AS (
        UPDATE agentd.artifacts SET html = NULL, expired_at = now()
         WHERE html IS NOT NULL AND updated_at < p_before
        RETURNING 1
    )
    SELECT count(*)::int FROM expired
$$;
